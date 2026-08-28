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
            this.settledIds = new Set(loaded.Channels.map((c) => c.Id));
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

            /*
                A channel made on this page is laid out now, in the same Save that created it.
                After the writes above, so the server is laying out the channel as it now
                stands, and before `settledIds` below, so the Week screen's reload - which
                watches exactly that - picks the new week up by itself.
            */
            for (const id of [...this.wantsLayout]) {
                this.wantsLayout.delete(id);
                const made = sent.Channels.find((c) => c.Id === id);
                if (!made || made.Sources.length === 0) { continue; }
                try {
                    await api().fetch({
                        url: api().getUrl('LiteTv/Channels/' + id + '/Week/Generate'),
                        type: 'POST',
                    });
                } catch (err) {
                    // Not worth failing the save over, and not worth an alert either: the
                    // channel IS saved, and the Week screen offers to lay it out with a button
                    // that says what went wrong if it is pressed again.
                    console.warn('[litetv] could not lay out the new channel', id, err);
                }
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
        // content, and asking the server to schedule nothing produces an error where the owner
        // expected a week.
        if (sources.length > 0) { this.wantsLayout.add(made.Id); }
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
