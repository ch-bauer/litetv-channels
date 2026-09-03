/*
 * The plugin configuration, held once for the whole app.
 *
 * Configuration edits are saved automatically. A session baseline is kept separately so the
 * owner can revert several automatic saves at once.
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
    /** Newly-created channels whose first schedule is still being generated. */
    scheduleGenerating = $state<string[]>([]);
    /** Generation failures are kept per channel so the empty schedule can offer retry. */
    scheduleGenerationErrors = $state<Record<string, string>>({});

    /** The configuration as the server last stated it. */
    private settled = $state<string>('');

    /** The state from when this page session was opened, used by Revert. */
    private sessionBaseline = $state<string>('');

    private autoSaveTimer: ReturnType<typeof setTimeout> | null = null;
    private autoSaveRunning = false;
    loaded = $state(false);

    /*
        The channel ids the SERVER holds, which is not the same list as the one on screen.

        A channel made here exists only in the page until Save, and the endpoints that answer
        about a channel - the stored week above all - answer 404 for one it has never been told
        about. Asked blindly, that 404 reaches the schedule as a raw error over an empty grid,
        which is exactly what a new channel used to look like. So the page can ask first.
    */
    private settledIds = $state<Set<string>>(new Set());

    /*
        Channels made here that should arrive with a week already laid out.

        A new channel used to be saved empty and stay blank until somebody found **Lay this week
        out** - so the one thing everybody wants next was a separate step nobody was told about.
        It cannot be done at the moment of creation: the server lays a week out from the STORED
        channel, and a channel made on this page does not exist there yet.

        So it is remembered here and done by Save, which is the moment the server first hears of
        the channel. That keeps the page's rule intact - nothing reaches the server until Save -
        and makes the layout part of the same action rather than a second write behind the
        owner's back.
    */
    private wantsLayout = new Set<string>();

    /**
     * Whether the server knows this channel at all - true once a Save has carried it across.
     * A channel it does not know has no week and cannot be given one.
     */
    serverHas(channelId: string | null): boolean {
        return channelId !== null && this.settledIds.has(channelId);
    }

    /** True while what is on screen differs from what the server holds. */
    readonly dirty = $derived(this.config !== null && stamp(this.config) !== this.settled);

    /** A reactive stamp used by the app to notice edits made by any control. */
    readonly editStamp = $derived(this.config === null ? '' : stamp(this.config));

    /** True when this session has made changes that can be reverted together. */
    readonly canRevert = $derived(this.config !== null
        && this.sessionBaseline !== ''
        && stamp(this.config) !== this.sessionBaseline);

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
            /*
                Two requests, because the channels are no longer part of the configuration
                document. They are a file each on the server - see Core/ChannelStore.cs - so
                that one unreadable channel costs one channel rather than all of them, and so a
                page that loaded before somebody else's edit cannot undo it by posting a whole
                list back.

                They are put back onto `config.Channels` here on purpose: the rest of the app
                reads them there and knows nothing about where they came from. Only `save`
                below has to care.
            */
            const [loaded, channels] = await Promise.all([
                api().getPluginConfiguration<PluginConfig>(PLUGIN_ID),
                api().getJSON<TvChannel[]>(api().getUrl('LiteTv/Definitions')),
            ]);

            loaded.Channels = channels ?? [];
            this.config = loaded;
            this.channelId = loaded.Channels[0]?.Id ?? null;
            this.settled = stamp(loaded);
            this.sessionBaseline = stamp(loaded);
            this.settledIds = new Set(loaded.Channels.map((c) => c.Id));
            this.loaded = true;
        } catch (err) {
            // Said out loud. A configuration that silently fails to load leaves a page that
            // looks merely empty, which is the failure this project keeps rediscovering.
            this.error = failureWords(err);
        } finally {
            this.loading = false;
        }
    }

    /**
     * Saves what has actually changed.
     *
     * One request per channel that differs, one delete per channel that has gone, and the
     * configuration document for everything that is not a channel. That is the shape the
     * server now wants: the channels live a file each, so this can no longer post the whole
     * list and hope.
     *
     * The channels are compared against `settled` - the state the server last stated - so a
     * screen full of channels nobody touched costs nothing, and a save cannot rewrite a
     * channel this page never edited.
     */
    async save(): Promise<void> {
        if (!this.config) { return; }
        const bar = dashboard();
        bar.showLoadingMsg();
        try {
            const sent = $state.snapshot(this.config);
            const before: PluginConfig | null = this.settled ? JSON.parse(this.settled) : null;
            const had = new Map((before?.Channels ?? []).map((c) => [c.Id, JSON.stringify(c)]));

            // Gone first, so a channel deleted and a channel added in the same save cannot
            // collide over a position.
            for (const id of had.keys()) {
                if (!sent.Channels.some((c) => c.Id === id)) {
                    await api().fetch({
                        url: api().getUrl('LiteTv/Definitions/' + id),
                        type: 'DELETE',
                    });
                }
            }

            for (const channel of sent.Channels) {
                if (had.get(channel.Id) === JSON.stringify(channel)) { continue; }
                await api().fetch({
                    url: api().getUrl('LiteTv/Definitions/' + channel.Id),
                    type: 'POST',
                    data: JSON.stringify(channel),
                    contentType: 'application/json',
                });
            }

            // Everything that is not a channel. The list is sent empty rather than omitted:
            // the server clears it anyway, and sending what is on screen would put the
            // channels back into the one document this change took them out of.
            await api().updatePluginConfiguration(PLUGIN_ID, { ...sent, Channels: [] });

            // Stamped from what was actually sent, so an edit made while the request was in
            // flight still counts as unsaved rather than being swallowed by the round trip.
            this.settled = JSON.stringify(sent);
            this.settledIds = new Set(sent.Channels.map((c) => c.Id));
            this.savedAt = new Date();

            // The channel is usable now. Laying out its first week can be expensive for a
            // collection or a long series, so let the page finish saving and do that work in
            // the background. The counter is reactive, which also tells Week to reload once
            // the generated week becomes available.
            const layoutIds = [...this.wantsLayout]
                .filter((id) => sent.Channels.some((c) => c.Id === id && c.Sources.length > 0));
            for (const id of layoutIds) { this.wantsLayout.delete(id); }
            if (layoutIds.length > 0) {
                for (const id of layoutIds) { this.beginScheduleGeneration(id); }
                void this.generateSchedules(layoutIds);
            }
        } catch (err) {
            bar.alert('Could not save: ' + (failureWords(err)));
        } finally {
            bar.hideLoadingMsg();
        }
    }

    /** Queues the current configuration for automatic persistence. */
    queueAutoSave(): void {
        if (!this.config || !this.loaded || !this.dirty) { return; }
        if (this.autoSaveTimer !== null) { clearTimeout(this.autoSaveTimer); }
        this.autoSaveTimer = setTimeout(() => {
            this.autoSaveTimer = null;
            if (this.autoSaveRunning || !this.dirty) { return; }
            this.autoSaveRunning = true;
            void this.save().finally(() => {
                this.autoSaveRunning = false;
                if (this.dirty) { this.queueAutoSave(); }
            });
        }, 450);
    }

    /** Restores the state from when this page session was opened. */
    async revert(): Promise<void> {
        if (!this.config || !this.canRevert) { return; }
        const target = JSON.parse(this.sessionBaseline) as PluginConfig;
        const current = JSON.parse(this.settled) as PluginConfig;
        const targetById = new Map(target.Channels.map((c) => [c.Id, c]));
        const currentById = new Map(current.Channels.map((c) => [c.Id, c]));
        const bar = dashboard();
        bar.showLoadingMsg();
        try {
            for (const id of currentById.keys()) {
                if (!targetById.has(id)) {
                    await api().fetch({ url: api().getUrl('LiteTv/Definitions/' + id), type: 'DELETE' });
                }
            }
            for (const channel of target.Channels) {
                if (JSON.stringify(currentById.get(channel.Id)) === JSON.stringify(channel)) { continue; }
                await api().fetch({
                    url: api().getUrl('LiteTv/Definitions/' + channel.Id),
                    type: 'POST',
                    data: JSON.stringify(channel),
                    contentType: 'application/json',
                });
            }
            await api().updatePluginConfiguration(PLUGIN_ID, { ...target, Channels: [] });
            this.config = target;
            this.settled = this.sessionBaseline;
            this.settledIds = new Set(target.Channels.map((c) => c.Id));
            this.savedAt = new Date();
        } catch (err) {
            bar.alert('Could not revert: ' + failureWords(err));
        } finally {
            bar.hideLoadingMsg();
        }
    }

    private async generateSchedules(ids: string[]): Promise<void> {
        const results = await Promise.allSettled(ids.map((id) => api().fetch({
            url: api().getUrl('LiteTv/Channels/' + id + '/Week/Generate'),
            type: 'POST',
        })));
        for (const [index, result] of results.entries()) {
            const id = ids[index];
            if (result.status === 'rejected') {
                const message = failureWords(result.reason);
                console.warn('[litetv] could not lay out the new channel', id, result.reason);
                this.scheduleGenerationErrors[id] = 'Schedule generation failed. ' + message;
            }
            this.scheduleGenerating = this.scheduleGenerating.filter((active) => active !== id);
        }
    }

    isScheduleGenerating(id: string): boolean { return this.scheduleGenerating.includes(id); }

    scheduleGenerationError(id: string): string | null {
        return this.scheduleGenerationErrors[id] ?? null;
    }

    retrySchedule(id: string): void {
        if (!this.serverHas(id) || this.isScheduleGenerating(id)) { return; }
        this.beginScheduleGeneration(id);
        void this.generateSchedules([id]);
    }

    private beginScheduleGeneration(id: string): void {
        if (!this.scheduleGenerating.includes(id)) {
            this.scheduleGenerating = [...this.scheduleGenerating, id];
        }
        delete this.scheduleGenerationErrors[id];
    }

    /** Adds a channel and selects it. Auto-save persists it shortly afterwards. */
    addChannel(name: string, sources: ChannelSource[] = []): void {
        if (!this.config) { return; }
        const made: TvChannel = {
            Id: newId(),
            // The server hands a new channel the end of the list; zero is how it is asked to.
            Position: 0,
            Name: name.trim() || 'New channel',
            Enabled: true,
            AnchorUtc: new Date().toISOString(),
            Sources: sources,
            Adverts: [],
            ScheduleEdits: [],
            EpisodesPerBlock: 1,
            Order: 'Sequential',
            RandomizeEpisodes: false,
            SameSourceProbability: 20,
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

        // Only when there is something to lay out. A channel started from nothing has no
        // content, and asking the server to schedule nothing produces an error.
        if (sources.length > 0) { this.wantsLayout.add(made.Id); }
    }

    /**
     * Deletes a channel immediately. Deleting is deliberately different from editing: there is
     * no useful undo for a channel and its stored week, so waiting for the page-wide Save button
     * only makes the UI claim that something can be undone when it cannot.
     */
    async removeChannel(channelId: string): Promise<void> {
        if (!this.config) { return; }
        const at = this.config.Channels.findIndex((c) => c.Id === channelId);
        if (at === -1) { return; }

        // A channel created on this page has no server file yet; removing it is local only.
        if (this.settledIds.has(channelId)) {
            const bar = dashboard();
            bar.showLoadingMsg();
            try {
                await api().fetch({
                    url: api().getUrl('LiteTv/Definitions/' + channelId),
                    type: 'DELETE',
                });
            } catch (err) {
                bar.alert('Could not delete: ' + failureWords(err));
                return;
            } finally {
                bar.hideLoadingMsg();
            }

            // Keep the server snapshot in step without swallowing unrelated edits.
            const settled = this.settled ? JSON.parse(this.settled) as PluginConfig : null;
            if (settled) {
                settled.Channels = settled.Channels.filter((c) => c.Id !== channelId);
                this.settled = stamp(settled);
            }
            const baseline = this.sessionBaseline ? JSON.parse(this.sessionBaseline) as PluginConfig : null;
            if (baseline) {
                baseline.Channels = baseline.Channels.filter((c) => c.Id !== channelId);
                this.sessionBaseline = stamp(baseline);
            }
            this.settledIds.delete(channelId);
        } else {
            this.wantsLayout.delete(channelId);
        }

        this.config.Channels.splice(at, 1);
        const next = this.config.Channels[at] ?? this.config.Channels[at - 1] ?? null;
        this.channelId = next ? next.Id : null;
    }
}

export const store = new ConfigStore();
