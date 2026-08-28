/*
 * The dashboard's own globals, typed.
 *
 * A Jellyfin plugin page is loaded into the dashboard's document rather than an iframe, so
 * `ApiClient` and `Dashboard` are simply there, already carrying the signed-in user's token.
 * That is the whole authentication story: the app never logs in, never stores a credential and
 * never asks for one. Anything that reaches for `fetch` directly has to put the token on by
 * hand - see `authHeaders` below, which exists because uploads cannot go through `ApiClient`.
 *
 * Both headers are sent for the same reason LiteTV's own SmartSimilarClient learned to send
 * them: a caller that authenticates with only one of the two reaches some plugins as nobody.
 */

export interface ApiClientLike {
    accessToken(): string;
    serverAddress(): string;
    getCurrentUserId(): string;
    getUrl(path: string, params?: Record<string, unknown>): string;
    getJSON<T = unknown>(url: string): Promise<T>;
    fetch<T = unknown>(options: {
        url: string;
        type?: string;
        dataType?: string;
        data?: unknown;
        contentType?: string;
        headers?: Record<string, string>;
    }): Promise<T>;
    getItems<T = unknown>(userId: string, query: Record<string, unknown>): Promise<T>;
    getPluginConfiguration<T = unknown>(id: string): Promise<T>;
    updatePluginConfiguration(id: string, config: unknown): Promise<unknown>;
}

export interface DashboardLike {
    alert(message: string | { message: string; title?: string }): void;
    showLoadingMsg(): void;
    hideLoadingMsg(): void;
    processPluginConfigurationUpdateResult?(result?: unknown): void;
}

declare global {
    interface Window {
        ApiClient?: ApiClientLike;
        Dashboard?: DashboardLike;
    }
}

/** The plugin's own guid, as the dashboard knows it. Must match Plugin.Id. */
export const PLUGIN_ID = '13953c97-f5a0-4713-8d4c-96b5369e5791';

export class NoDashboardError extends Error {
    constructor() {
        // Said plainly, because the one way to hit this is to open the bundle on its own
        // instead of injecting it into a dashboard - and then nothing works for a reason
        // that is not otherwise visible.
        super(
            'This page is part of the Jellyfin dashboard and cannot run on its own: '
            + 'the dashboard supplies the server connection and the signed-in user.',
        );
        this.name = 'NoDashboardError';
    }
}

export function api(): ApiClientLike {
    const client = window.ApiClient;
    if (!client) { throw new NoDashboardError(); }
    return client;
}

export function dashboard(): DashboardLike {
    // Not fatal on its own: Dashboard is only chrome - alerts and the spinner - so a missing
    // one degrades to the console rather than stopping the app.
    return window.Dashboard ?? {
        alert: (m) => console.warn('[litetv]', m),
        showLoadingMsg: () => { },
        hideLoadingMsg: () => { },
    };
}

export function hasDashboard(): boolean {
    return Boolean(window.ApiClient);
}

/** For the few calls that must use `fetch` directly, such as uploading a picture. */
export function authHeaders(): Record<string, string> {
    const token = api().accessToken();
    return {
        'Authorization': 'MediaBrowser Token="' + token + '"',
        'X-Emby-Token': token,
    };
}

/** An absolute URL for something the server returned as a path. */
export function absolute(url: string): string {
    return /^https?:/i.test(url) ? url : api().serverAddress() + url;
}

/**
 * What went wrong, in words a person can act on.
 *
 * Written because every failure on this page used to read **`[object Response]`**. Jellyfin's
 * `ApiClient` rejects with the `Response` itself rather than an `Error`, so the idiom the whole
 * app used - `failureWords(err)` - fell through to `String()` on
 * an object whose `toString` is exactly that. "Could not save: [object Response]" is what the
 * owner saw when a channel could not be created, and it named neither the code nor the route,
 * so the one useful fact - a **500** - had to be found by hand afterwards.
 *
 * The body is not read: it would make this asynchronous, and every caller is a `catch` that
 * wants a string now. The status and the route are what identify the fault anyway.
 */
export function failureWords(err: unknown): string {
    if (typeof Response !== 'undefined' && err instanceof Response) {
        const where = err.url ? ' from ' + err.url.replace(/^https?:\/\/[^/]+/, '') : '';
        const why = err.status === 500
            // Said outright, because it is nearly always this: the configuration is posted as
            // one document, and one value the server cannot read fails the whole of it.
            ? 'the server could not process it - a value somewhere in the configuration may be one it cannot read'
            : err.status === 404
                ? 'the server has no such thing - an unsaved channel looks exactly like this'
                : err.statusText || 'the server refused it';
        return 'HTTP ' + err.status + where + ': ' + why;
    }

    return err instanceof Error ? err.message : String(err);
}
