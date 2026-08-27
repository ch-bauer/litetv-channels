/*
 * The plugin configuration, held once for the whole app.
 *
 * Nothing here saves as you type. The old page's rule is kept because it is the right one: an
 * edit changes what is on this screen, and **Save** is what reaches the server. `dirty` is what
 * the header reads, so "saved a moment ago" can never be a lie.
 */
import { api, dashboard, PLUGIN_ID } from './jellyfin';
import type { ChannelSource, PluginConfig, TvChannel } from './types';

class ConfigStore {
    config = $state<PluginConfig | null>(null);
    loading = $state(false);
    error = $state<string | null>(null);
    dirty = $state(false);
    savedAt = $state<Date | null>(null);

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
            this.dirty = false;
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
            await api().updatePluginConfiguration(PLUGIN_ID, $state.snapshot(this.config));
            this.dirty = false;
            this.savedAt = new Date();
        } catch (err) {
            bar.alert('Could not save: ' + (err instanceof Error ? err.message : String(err)));
        } finally {
            bar.hideLoadingMsg();
        }
    }

    /** Every edit goes through here, so nothing can change the config without marking it. */
    touch(): void {
        this.dirty = true;
    }

    /**
     * Adds a channel and selects it. `sources` empty is "start from nothing"; the suggestions
     * screen passes a lineup. Not saved - Save is still what reaches the server.
     */
    addChannel(name: string, sources: ChannelSource[] = []): void {
        if (!this.config) { return; }
        const made: TvChannel = {
            Id: crypto.randomUUID(),
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
        this.touch();
    }
}

export const store = new ConfigStore();
