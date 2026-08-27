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
import {
    MAX_WEEKS, applyEdits, editWords, getWeek,
    type Week, type WeekAiring, type WeekEdit,
} from '../api/week';

export type View = 'week' | 'day';

const ZOOM_KEY = 'litetv.week.zoom';

class WeekStore {
    /** The week as the pending edits leave it: what is drawn, and what Save would store. */
    week = $state<Week | null>(null);
    loading = $state(false);
    error = $state<string | null>(null);
    busy = $state(false);

    /** Edits made and not yet saved, oldest first. */
    pending = $state<WeekEdit[]>([]);

    view = $state<View>('week');
    /** Pixels per hour, kept per view: a week is read at a glance, a day is read closely. */
    zoomFor = $state<Record<View, number>>({ week: 46, day: 170 });

    /** The airing being inspected, by id. Null is a real state: nothing selected. */
    selectedId = $state<string | null>(null);

    /*
        Which week of the cycle is being looked at, counting from zero.

        A schedule of four weeks is twenty-eight day columns, and nobody can find Thursday in
        that. So the grid still draws seven days and this says which seven - the same way a
        calendar shows one month of a year rather than all twelve at once.
    */
    weekIndex = $state(0);

    private channelId: string | null = null;

    get zoom(): number {
        return this.zoomFor[this.view];
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

    setZoom(value: number): void {
        this.zoomFor[this.view] = value;
        try {
            localStorage.setItem(ZOOM_KEY, JSON.stringify({ ...this.zoomFor, view: this.view }));
        } catch {
            // How far somebody has zoomed in is not worth an error.
        }
    }

    restoreZoom(): void {
        try {
            const stored = JSON.parse(localStorage.getItem(ZOOM_KEY) ?? 'null');
            if (!stored) { return; }
            for (const view of ['week', 'day'] as View[]) {
                const value = Number(stored[view]);
                // The bounds must match the slider's, or a remembered zoom is silently clamped
                // away and the setting appears not to stick.
                if (Number.isFinite(value) && value >= 8 && value <= 1200) {
                    this.zoomFor[view] = value;
                }
            }
            if (stored.view === 'week' || stored.view === 'day') { this.view = stored.view; }
        } catch {
            // Nothing remembered, or something else wrote the key. The defaults are fine.
        }
    }

    /** Clicking what is already selected clears it, which the old page never allowed. */
    toggle(id: string | null): void {
        this.selectedId = this.selectedId === id ? null : id;
    }

    async load(channelId: string): Promise<void> {
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
        try {
            this.week = await getWeek(channelId);
        } catch (err) {
            this.error = err instanceof Error ? err.message : String(err);
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
            this.error = err instanceof Error ? err.message : String(err);
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
                this.error = err instanceof Error ? err.message : String(err);
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
            this.error = err instanceof Error ? err.message : String(err);
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

    async generate(): Promise<void> {
        this.selectedId = null;
        await this.add({ Kind: 'Generate' });
    }

    async clear(): Promise<void> {
        this.selectedId = null;
        await this.add({ Kind: 'Clear' });
    }

    /** Takes one airing off the week. Drawn as gone immediately; pending until Save. */
    async remove(airingId: string): Promise<void> {
        await this.add({ Kind: 'Remove', AiringId: airingId }, () => {
            if (!this.week) { return; }
            this.week.Airings = this.week.Airings.filter((a) => a.Id !== airingId);
            if (this.selectedId === airingId) { this.selectedId = null; }
        });
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
