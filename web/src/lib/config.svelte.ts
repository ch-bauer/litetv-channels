/*
 * The plugin configuration, held once for the whole app.
 *
 * Nothing here saves as you type. The old page's rule is kept because it is the right one: an
 * edit changes what is on this screen, and **Save** is what reaches the server. `dirty` is what
 * the header reads, so "saved a moment ago" can never be a lie.
 *
 * `dirty` is MEASURED, not announced. It used to be a flag every edit had to remember to set,
 * and an edit that forgot left Save greyed out over a real change - the owner hit exactly that.
 * Now it is the live configuration compared against what the server last gave us, so a control
 * cannot fail to mark the page dirty, and putting a value back the way it was un-marks it.
 */
import { api, dashboard, PLUGIN_ID, failureWords } from './jellyfin';
import { newId } from './ids';
import type { ChannelSource, PluginConfig, TvChannel } from './types';

/**
 * A configuration as one string.
 *
 * `JSON.stringify` reads every field straight through the state proxy, and a read inside a
 * derived is a dependency - so `dirty` depends on the whole configuration, however deep and
 * however new the field. Done directly rather than through `$state.snapshot` because the
 * tracking then depends on nothing but the property reads themselves.
 */
function stamp(config: PluginConfig): string {
    return JSON.stringify(config);
}

class ConfigStore {
    config = $state<PluginConfig | null>(null);
    loading = $state(false);
    error = $state<string | null>(null);
    savedAt = $state<Date | null>(null);

    /** The configuration as the server last stated it - what `dirty` is measured against. */
    private settled = $state<string>('');

    /*
        The channel ids the SERVER holds, which is not the same list as the one on screen.

        A channel made here exists only in the page until Save, and the endpoints that answer
        about a channel - the stored week above all - answer 404 for one it has never been told
        about. Asked blindly, that 404 reaches the schedule as a raw error over an empty grid,
        which is exactly what a new channel used to look like. So the page can ask first.
    */
    private settledIds = $state<Set<string>>(new Set());

    /**
     * Whether the server knows this channel at all - true once a Save has carried it across.
     * A channel it does not know has no week and cannot be given one.
     */
    serverHas(channelId: string | null): boolean {
        return channelId !== null && this.settledIds.has(channelId);
    }

    /** True while what is on screen differs from what the server holds. */
    readonly dirty = $derived(this.config !== null && stamp(this.config) !== this.settled);

    /** The channel currently being edited. */
    channelId = $state<string | null>(null);

    get channels(): TvChannel[] {
        return this.config?.Channels ?? [];
    }

    get channel(): TvChannel | null {
        const list = this.channels;
        if (list.length === 0) { return null; }
        return list.find((c) => c.Id === this.channelId) ?? list[0];
    }

    async load(): Promise<void> {
        this.loading = true;
        this.error = null;
        try {
            const loaded = await api().getPluginConfiguration<PluginConfig>(PLUGIN_ID);
            this.config = loaded;
            this.channelId = loaded.Channels[0]?.Id ?? null;
            this.settled = stamp(loaded);
            this.settledIds = new Set(loaded.Channels.map((c) => c.Id));
        } catch (err) {
            // Said out loud. A configuration that silently fails to load leaves a page that
            // looks merely empty, which is the failure this project keeps rediscovering.
            this.error = failureWords(err);
        } finally {
            this.loading = false;
        }
    }

    async save(): Promise<void> {
        if (!this.config) { return; }
        const bar = dashboard();
        bar.showLoadingMsg();
        try {
            const sent = $state.snapshot(this.config);
            await api().updatePluginConfiguration(PLUGIN_ID, sent);
            // Stamped from what was actually sent, so an edit made while the request was in
            // flight still counts as unsaved rather than being swallowed by the round trip.
            this.settled = JSON.stringify(sent);
            this.settledIds = new Set(sent.Channels.map((c) => c.Id));
            this.savedAt = new Date();
        } catch (err) {
            bar.alert('Could not save: ' + (failureWords(err)));
        } finally {
            bar.hideLoadingMsg();
        }
    }

    /**
     * Adds a channel and selects it. `sources` empty is "start from nothing"; the suggestions
     * screen passes a lineup. Not saved - Save is still what reaches the server.
     */
    addChannel(name: string, sources: ChannelSource[] = []): void {
        if (!this.config) { return; }
        const made: TvChannel = {
            Id: newId(),
            Name: name.trim() || 'New channel',
            Enabled: true,
            AnchorUtc: new Date().toISOString(),
            Sources: sources,
            Adverts: [],
            ScheduleEdits: [],
            EpisodesPerBlock: 1,
            Order: 'Sequential',
            SlotMinutes: 0,
            TrailersInGaps: true,
            Trailers: 'Off',
            TrailerEveryPrograms: 3,
            TrailerLookahead: 3,
            TrailerTitles: [],
            Blocks: [],
            TrailerSlots: [],
            Artwork: {},
        };
        this.config.Channels.push(made);
        this.channelId = made.Id;
    }

    /**
     * Takes a channel out of the configuration and selects whatever is beside it.
     *
     * Not saved, like everything else here - Save is what reaches the server. The channel's
     * stored week lives in its own file rather than in the configuration, and the server throws
     * that away when it sees the channel has gone.
     */
    removeChannel(channelId: string): void {
        if (!this.config) { return; }
        const at = this.config.Channels.findIndex((c) => c.Id === channelId);
        if (at === -1) { return; }

        this.config.Channels.splice(at, 1);
        const next = this.config.Channels[at] ?? this.config.Channels[at - 1] ?? null;
        this.channelId = next ? next.Id : null;
    }
}

export const store = new ConfigStore();
