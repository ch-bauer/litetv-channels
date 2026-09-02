/*
 * The week being looked at, and everything that can be done to it.
 *
 * Held apart from the plugin configuration because a week is stored server-side in its own file
 * and edited through its own endpoints - but it is no longer SAVED apart from it. Until 28 Aug
 * 2026 every schedule edit was written down the instant it was made, and the owner's report was
 * the plain consequence: "removing stuff from schedule does not light up save". One page with
 * one Save button and two different rules about when work is written down is a page that cannot
 * be trusted, and there was no way back from a mis-drop.
 *
 * So an edit here is now PENDING. It is added to a list, the server is asked what the list
 * comes to, and that is what the grid draws. Save commits the same list; Undo takes the last
 * one off; leaving the page throws them away, which is the same promise the rest of the page
 * makes.
 *
 * The rehearsal has to come from the server. What an edit does to the rows around it is the
 * server's arithmetic - an appointment trims its neighbours, cuts one in two, drops one that it
 * covers - and a page drawing its own guess at that would be showing a week nobody was going to
 * get. So every edit costs one round trip, exactly as it did when every edit was a write.
 */
import { failureWords } from '../jellyfin';
import {
    MAX_WEEKS, SECONDS_PER_DAY, applyEdits, editWords, getWeek, nowSecond,
    type Week, type WeekAiring, type WeekEdit,
} from '../api/week';

export type View = 'week' | 'day';

const ZOOM_KEY = 'litetv.week.zoom';

const VIEWS: View[] = ['week', 'day'];

/** Where a view starts before anybody has zoomed it: a week at a glance, a day up close. */
const DEFAULT_ZOOM: Record<View, number> = { week: 46, day: 170 };

/*
    The slider's own bounds. A remembered zoom outside them would be clamped away on the way to
    the screen, and the setting would look like it had not stuck - so they are checked here, on
    the way in, and the slider must not disagree with them.

    Exported, because they were written out by hand in three places - here, the slider's own
    `min`/`max`, and the clamp on the zoom "Zoom to now" works out - and three copies of a
    bound is two chances for them to drift apart.

    The ceiling is 2400 rather than 1200 because 1200 was not high enough to reach the rule
    "Zoom to now" is asked to obey. It keeps at least half an hour of schedule on screen, which
    on a 750-pixel grid is 1500 pixels an hour and on a taller window is more - so the ask fell
    off the end of the scale, the slider pinned at its maximum, and a short programme still got
    the most zoomed-in view the page had. It is reachable now.
*/
export const ZOOM_MIN = 8;
export const ZOOM_MAX = 2400;

function sane(value: number): boolean {
    return Number.isFinite(value) && value >= ZOOM_MIN && value <= ZOOM_MAX;
}

class WeekStore {
    /** The week as the pending edits leave it: what is drawn, and what Save would store. */
    week = $state<Week | null>(null);
    loading = $state(false);
    error = $state<string | null>(null);

    /*
        True for a channel the page has made and nobody has saved yet.

        Not an error, though it used to arrive as one. A week belongs to a channel the SERVER
        holds, so every week endpoint answers 404 for a channel that exists only on screen - and
        that 404 was printed raw above an empty grid, which is what "creating a channel shows an
        error on the schedule" was. There is nothing wrong: the channel simply has not been sent
        yet, and Save is the whole remedy.
    */
    unsaved = $state(false);
    busy = $state(false);

    /** Edits made and not yet saved, oldest first. */
    pending = $state<WeekEdit[]>([]);

    view = $state<View>('week');

    /*
        Pixels per hour, kept per CHANNEL and per view.

        Per view because a week is read at a glance and a day is read closely. Per channel
        because the channels are not alike: a film channel is four bars a day and a channel of
        every SpongeBob episode is forty, and a zoom that suits one is useless on the other.
        Carrying one number between them meant every channel change was followed by a
        correction.
    */
    zoomByChannel = $state<Record<string, Partial<Record<View, number>>>>({});

    /*
        Ticked per channel: keep the zoom sized so what is on air right now is properly visible,
        rather than a sliver somewhere.

        On unless the channel says otherwise - a channel opened for the first time should show
        the programme playing at a size you can read, and no fixed number can do that for both a
        film channel of four bars a day and a channel of forty.

        It is the SECOND of the two toggles the owner asked for, and deliberately separate from
        "Follow what's on": one decides how big things are, the other decides where the grid is
        scrolled to. Wanting one without the other is perfectly reasonable in both directions.
    */
    zoomToNowByChannel = $state<Record<string, boolean>>({});

    /** The last zoom set by hand, kept only to fill the older single-zoom keys on the way out. */
    private lastManual: Partial<Record<View, number>> = {};

    /*
        Ticked per channel: hold the zoom and the scroll on whatever is on air right now.

        On unless the channel says otherwise, because it is the right thing on arriving at a
        channel - the question a schedule is opened with is nearly always "what is on?". An
        empty record therefore means ticked, and unticking has to be WRITTEN DOWN as false
        rather than left absent.
    */
    frameNowByChannel = $state<Record<string, boolean>>({});

    /** The airing being inspected, by id. Null is a real state: nothing selected. */
    selectedId = $state<string | null>(null);

    /*
        Which week of the cycle is being looked at, counting from zero.

        A schedule of four weeks is twenty-eight day columns, and nobody can find Thursday in
        that. So the grid still draws seven days and this says which seven - the same way a
        calendar shows one month of a year rather than all twelve at once.
    */
    weekIndex = $state(0);

    /*
        $state, and this is not a detail - it is the whole of "the zoom on day does nothing".

        It was a plain field. Everything keyed off the channel - the zoom, both toggles, and so
        `pxPerSecond` and `dayHeight` in the grid - reads `channelKey`, which reads this. A plain
        field is invisible to Svelte, so those deriveds ran ONCE, while the channel was still
        null, keyed off '' and settled on DEFAULT_ZOOM. Nothing ever invalidated them again: the
        slider wrote a real number under a real channel key, and the grid went on reading a key
        that was empty when it last looked.

        `view` IS $state, which is exactly why switching to Week and back "helped" - it was the
        only thing that could invalidate the derived, and on re-running it finally saw the real
        channel.
    */
    private channelId = $state<string | null>(null);

    /*
        What the last load was for, as channel and saved-ness. `load` is driven by an effect, so
        it runs again whenever anything it reads changes; without this, a load provoked by Save
        would land after a pending edit was made and throw it away. Same key, nothing to do.
    */
    private loadedKey: string | null = null;

    /** The channel the store is holding, as a key. Empty before anything is loaded. */
    get channelKey(): string {
        return this.channelId ?? '';
    }

    /*
        ONE source of truth for the zoom, and this is the whole of the fix for "the zoom on day
        does nothing".

        There used to be two - a worked-out `autoZoom` and the slider's `zoomByChannel` - and the
        getter preferred `autoZoom`, which is the opposite of what its own comment claimed. So a
        hand on the slider wrote a number that a stale worked-out one went on beating, the grid
        kept the zoom it opened at, and switching view and back - which changed which key was
        looked up - "helped". Whichever of the two was ahead depended on the order effects
        happened to run in, so it was never going to be reliable.

        Now: the slider writes here, `zoomToNow` writes here, and the getter reads here.
    */
    get zoom(): number {
        return this.zoomByChannel[this.channelKey]?.[this.view] ?? DEFAULT_ZOOM[this.view];
    }

    /**
     * The zoom worked out from what is on air, applied without taking the toggle off - which is
     * what separates it from {@link setZoom}, the hand on the slider.
     */
    applyZoomToNow(value: number): void {
        const key = this.channelKey;
        this.zoomByChannel[key] = { ...this.zoomByChannel[key], [this.view]: value };
    }

    /** Whether this channel keeps the zoom sized so what is on air is properly visible. */
    get zoomToNow(): boolean {
        return this.zoomToNowByChannel[this.channelKey] ?? true;
    }

    setZoomToNow(on: boolean): void {
        this.zoomToNowByChannel[this.channelKey] = on;
        this.remember();
    }

    /** Whether this channel is following what is on air. Ticked until told otherwise. */
    get frameNow(): boolean {
        return this.frameNowByChannel[this.channelKey] ?? true;
    }

    /*
        Which day of the cycle the Day view shows: today when today is in the week being looked
        at, and that week's Monday otherwise - there is no "today" in next week.

        Here rather than in the grid because the status line under the grid has to name the same
        day the grid is drawing, and two implementations of that sum would eventually disagree.
    */
    get shownDay(): number {
        if (this.weekIndex !== this.currentWeek) { return this.weekIndex * 7; }
        return this.currentWeek * 7 + Math.floor(nowSecond() / SECONDS_PER_DAY);
    }

    setFrameNow(on: boolean): void {
        this.frameNowByChannel[this.channelKey] = on;
        this.remember();
    }

    get airings(): WeekAiring[] {
        return this.week?.Airings ?? [];
    }

    get selected(): WeekAiring | null {
        if (this.selectedId === null) { return null; }
        return this.airings.find((a) => a.Id === this.selectedId) ?? null;
    }

    /** How many weeks this channel's schedule runs for before it repeats. */
    get weeks(): number {
        return Math.max(1, this.week?.Weeks ?? 1);
    }

    /** Which week of the cycle is actually on air now, counting from zero. */
    get currentWeek(): number {
        return this.week?.CurrentWeek ?? 0;
    }

    /** True while the schedule on screen is not the schedule the server holds. */
    get dirty(): boolean {
        return this.pending.length > 0;
    }

    /** What Undo would take back, in words, or null when there is nothing to take back. */
    get undoWords(): string | null {
        const last = this.pending[this.pending.length - 1];
        return last ? editWords(last) : null;
    }

    /** The slider. A hand on it beats anything worked out, on this channel and this view. */
    setZoom(value: number): void {
        const key = this.channelKey;
        this.zoomByChannel[key] = { ...this.zoomByChannel[key], [this.view]: value };

        // A hand on the slider takes the wheel: nothing may size the grid from what is on air
        // while somebody is setting it themselves, or the number would be put back underneath
        // them. Following is left alone - you can follow what is on at any zoom you like.
        this.zoomToNowByChannel[key] = false;

        this.lastManual[this.view] = value;
        this.remember();
    }

    private remember(): void {
        try {
            localStorage.setItem(ZOOM_KEY, JSON.stringify({
                // The older single-zoom keys are still written, so a downgrade to a build that
                // only knows that shape still finds a zoom it understands.
                ...this.lastManual,
                view: this.view,
                channels: Object.fromEntries(
                    Object.keys({
                        ...this.zoomByChannel,
                        ...this.frameNowByChannel,
                        ...this.zoomToNowByChannel,
                    })
                        .filter((id) => id !== '')
                        .map((id) => [id, {
                            ...this.zoomByChannel[id],
                            frameNow: this.frameNowByChannel[id] === true,
                            zoomToNow: this.zoomToNowByChannel[id] === true,
                        }]),
                ),
            }));
        } catch {
            // How far somebody has zoomed in is not worth an error.
        }
    }

    restoreZoom(): void {
        try {
            const stored = JSON.parse(localStorage.getItem(ZOOM_KEY) ?? 'null');
            if (!stored) { return; }
            if (stored.view === 'week' || stored.view === 'day') { this.view = stored.view; }

            for (const view of VIEWS) {
                const value = Number(stored[view]);
                if (sane(value)) { this.lastManual[view] = value; }
            }

            const channels: unknown = stored.channels;
            if (!channels || typeof channels !== 'object') { return; }
            for (const [id, held] of Object.entries(channels as Record<string, unknown>)) {
                const record = (held ?? {}) as Record<string, unknown>;
                const kept: Partial<Record<View, number>> = {};
                for (const view of VIEWS) {
                    const value = Number(record[view]);
                    if (sane(value)) { kept[view] = value; }
                }
                if (Object.keys(kept).length > 0) { this.zoomByChannel[id] = kept; }
                // Both values are kept: the box is ticked by default, so an unticked channel
                // is only unticked if the false was written down and read back.
                if (typeof record.frameNow === 'boolean') {
                    this.frameNowByChannel[id] = record.frameNow;
                }

                // Same rule for the second box: ticked by default, so only a written-down
                // false unticks it.
                if (typeof record.zoomToNow === 'boolean') {
                    this.zoomToNowByChannel[id] = record.zoomToNow;
                }
            }
        } catch {
            // Nothing remembered, or something else wrote the key. The defaults are fine.
        }
    }

    /** Clicking what is already selected clears it, which the old page never allowed. */
    toggle(id: string | null): void {
        this.selectedId = this.selectedId === id ? null : id;
    }

    /**
     * Loads a channel's week.
     *
     * `onServer` is the caller's answer to "has this channel been saved?" - the configuration
     * store knows, and the week endpoints only answer about channels the server holds.
     */
    async load(channelId: string, onServer = true, force = false): Promise<void> {
        const key = channelId + '|' + (onServer ? 'saved' : 'new');
        if (!force && this.loadedKey === key) { return; }
        this.loadedKey = key;
        this.channelId = channelId;
        this.loading = true;
        this.error = null;
        this.selectedId = null;
        this.weekIndex = 0;
        // Pending edits belong to the channel they were made on. Carrying them to the next
        // channel would apply them to somebody else's schedule, which is the worst thing this
        // store could do.
        this.pending = [];
        /*
            And the week itself goes at once, rather than when the answer lands.

            A channel made from nothing has no week, and what was on screen until the request
            came back was the LAST channel's - which reads as a new channel arriving with
            somebody else's schedule already in it.
        */
        this.week = null;
        this.unsaved = !onServer;
        if (!onServer) {
            // Nothing to ask for, and asking would only produce a 404 to print.
            this.loading = false;
            return;
        }
        try {
            this.week = await getWeek(channelId);
        } catch (err) {
            this.error = failureWords(err);
        } finally {
            this.loading = false;
        }
    }

    /**
     * Asks the server what the pending run comes to, and draws it.
     *
     * `commit` true writes it down as well. Nothing is written on any other path, so the only
     * way for a schedule edit to reach the server is Save.
     */
    private async rehearse(commit = false): Promise<boolean> {
        const id = this.channelId;
        if (id === null) { return false; }
        this.busy = true;
        this.error = null;
        try {
            this.week = await applyEdits(id, $state.snapshot(this.pending) as WeekEdit[], commit);
            return true;
        } catch (err) {
            this.error = failureWords(err);
            return false;
        } finally {
            this.busy = false;
        }
    }

    /**
     * Adds an edit and redraws.
     *
     * The optimistic half is kept for the two edits whose effect on the row itself is obvious -
     * a removal and a move - so the grid answers the mouse at once and the server's version
     * lands a moment later. Anything the server does to the NEIGHBOURS still waits for the
     * answer, because that is the part a page cannot know.
     */
    private async add(edit: WeekEdit, optimistic?: () => void): Promise<void> {
        this.pending.push(edit);
        optimistic?.();
        await this.rehearse();
    }

    /** Takes the last edit back. The rest of the run is re-asked, so the answer stays honest. */
    async undo(): Promise<void> {
        if (this.pending.length === 0) { return; }
        this.pending.pop();
        this.selectedId = null;
        if (this.pending.length === 0) {
            // Nothing left to rehearse: the server's own week is the answer, and asking for it
            // costs the same round trip as an empty run would.
            const id = this.channelId;
            if (id === null) { return; }
            this.busy = true;
            try {
                this.week = await getWeek(id);
            } catch (err) {
                this.error = failureWords(err);
            } finally {
                this.busy = false;
            }
            return;
        }
        await this.rehearse();
    }

    /** Throws every pending edit away and goes back to the stored week. */
    async discard(): Promise<void> {
        if (this.pending.length === 0) { return; }
        this.pending = [];
        this.selectedId = null;
        const id = this.channelId;
        if (id === null) { return; }
        this.busy = true;
        try {
            this.week = await getWeek(id);
        } catch (err) {
            this.error = failureWords(err);
        } finally {
            this.busy = false;
        }
    }

    /**
     * Writes the pending run down. Called by Save.
     *
     * The list is only emptied when the server has answered, so a failed save leaves the work
     * on screen and still pending rather than losing it quietly.
     */
    async commit(): Promise<void> {
        if (this.pending.length === 0) { return; }
        if (await this.rehearse(true)) {
            this.pending = [];
        }
    }

    /**
     * How many weeks the schedule runs for.
     *
     * Pending like every other schedule edit, so it can be undone - which matters more here
     * than anywhere else on the screen, because making a fortnight a week again throws the
     * second week away.
     */
    async setLength(weeks: number): Promise<void> {
        const wanted = Math.max(1, Math.min(MAX_WEEKS, Math.round(weeks)));
        if (wanted === this.weeks) { return; }
        // Looking at week four of a four-week schedule that has just become a fortnight would
        // draw seven days of nothing that no longer exist.
        if (this.weekIndex >= wanted) { this.weekIndex = wanted - 1; }
        this.selectedId = null;
        await this.add({ Kind: 'Length', Weeks: wanted });
    }

    /**
     * Makes the schedule as long as the channel's content, and lays it out over that.
     *
     * This is what a schedule longer than a week is FOR. A channel of every episode of a series
     * should air all of them and then start again; a week-long schedule airs the first week's
     * worth for ever and never reaches the rest, with nothing anywhere saying so. How many weeks
     * that needs is the server's to work out - it already measures how long a channel takes to
     * play everything once.
     *
     * The lay-out goes with it in the same run, because fitting the length on its own leaves the
     * new weeks empty and the channel dark in them.
     */
    async fitToContent(): Promise<void> {
        this.selectedId = null;
        this.pending.push({ Kind: 'FitLength' }, { Kind: 'Generate' });
        await this.rehearse();
    }

    async generate(): Promise<void> {
        this.selectedId = null;
        await this.add({ Kind: 'Generate' });
    }

    async clear(): Promise<void> {
        this.selectedId = null;
        await this.add({ Kind: 'Clear' });
    }

    /**
     * Takes one airing off the week. Drawn as gone immediately; pending until Save.
     *
     * The selection moves to what follows rather than being dropped. Clearing it meant that
     * deleting a run of programmes was delete, find the next one, click it, delete - when the
     * point of having a keyboard shortcut is to press the key again.
     */
    async remove(airingId: string): Promise<void> {
        const successor = this.after(airingId);
        await this.add({ Kind: 'Remove', AiringId: airingId }, () => {
            if (!this.week) { return; }
            this.week.Airings = this.week.Airings.filter((a) => a.Id !== airingId);
            if (this.selectedId === airingId) { this.selectedId = successor; }
        });
    }

    /**
     * What to select once this airing is gone: the next real programme in the week, or the
     * previous one when it was the last. Null only when nothing else is left.
     *
     * Gaps are skipped - they have no id and cannot be selected - and the answer is worked out
     * BEFORE the removal, while the week still holds the row being taken out.
     */
    private after(airingId: string): string | null {
        const rows = this.airings
            .filter((a) => a.Kind !== 'Gap' && a.Id !== null)
            .sort((a, b) => a.StartSecond - b.StartSecond);
        const at = rows.findIndex((a) => a.Id === airingId);
        if (at === -1) { return null; }
        return rows[at + 1]?.Id ?? rows[at - 1]?.Id ?? null;
    }

    /** Moves or adds one airing. */
    async place(airing: Partial<WeekAiring>): Promise<void> {
        await this.add({ Kind: 'Place', Airing: airing }, () => {
            if (!this.week || !airing.Id) { return; }
            const at = this.week.Airings.findIndex((a) => a.Id === airing.Id);
            if (at !== -1) {
                this.week.Airings[at] = { ...this.week.Airings[at], ...airing } as WeekAiring;
            }
        });
    }

    /**
     * Adds several rows in one go, as one round trip.
     *
     * A playlist dropped on the week is a run of programmes, and sending them one at a time
     * would be one rehearsal per video - and each one would be laid against a week the previous
     * one had already bent.
     */
    async placeMany(airings: Partial<WeekAiring>[]): Promise<void> {
        if (airings.length === 0) { return; }
        for (const airing of airings) {
            this.pending.push({ Kind: 'Place', Airing: airing });
        }
        await this.rehearse();
    }
}

export const week = new WeekStore();
