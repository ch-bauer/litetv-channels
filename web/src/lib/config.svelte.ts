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
import { api, dashboard, PLUGIN_ID } from './jellyfin';
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
        } catch (err) {
            // Said out loud. A configuration that silently fails to load leaves a page that
            // looks merely empty, which is the failure this project keeps rediscovering.
            this.error = err instanceof Error ? err.message : String(err);
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
            this.savedAt = new Date();
        } catch (err) {
            bar.alert('Could not save: ' + (err instanceof Error ? err.message : String(err)));
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
            Trailers: 'Between',
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
}

export const store = new ConfigStore();
