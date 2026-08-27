/*
 * The week being looked at, and everything that can be done to it.
 *
 * Held apart from the plugin configuration on purpose: a week is stored server-side in its own
 * file and edited through its own endpoints, and it is saved the moment you change it - unlike
 * the configuration, which waits for Save. Mixing the two is how "saved a moment ago" starts
 * lying.
 *
 * Every mutation is **optimistic**: the change is drawn at once and the server's answer replaces
 * it when it lands. The owner's complaint that taking a programme off the week lags was exactly
 * this - a round trip before anything moved.
 */
import {
    clearWeek, deleteAiring, generateWeek, getWeek, putAiring,
    type Week, type WeekAiring,
} from '../api/week';

export type View = 'week' | 'day';

const ZOOM_KEY = 'litetv.week.zoom';

class WeekStore {
    week = $state<Week | null>(null);
    loading = $state(false);
    error = $state<string | null>(null);
    busy = $state(false);

    view = $state<View>('week');
    /** Pixels per hour, kept per view: a week is read at a glance, a day is read closely. */
    zoomFor = $state<Record<View, number>>({ week: 46, day: 170 });

    /** The airing being inspected, by id. Null is a real state: nothing selected. */
    selectedId = $state<string | null>(null);

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
        try {
            this.week = await getWeek(channelId);
        } catch (err) {
            this.error = err instanceof Error ? err.message : String(err);
        } finally {
            this.loading = false;
        }
    }

    private async run(
        act: (channelId: string) => Promise<Week>,
        optimistic?: () => void,
        undo?: () => void,
    ): Promise<void> {
        const id = this.channelId;
        if (id === null) { return; }
        optimistic?.();
        this.busy = true;
        this.error = null;
        try {
            this.week = await act(id);
        } catch (err) {
            undo?.();
            this.error = err instanceof Error ? err.message : String(err);
        } finally {
            this.busy = false;
        }
    }

    async generate(): Promise<void> {
        await this.run(generateWeek);
        this.selectedId = null;
    }

    async clear(): Promise<void> {
        await this.run(clearWeek);
        this.selectedId = null;
    }

    /** Takes one airing off the week. Drawn as gone immediately; put back if the server refuses. */
    async remove(airingId: string): Promise<void> {
        const before = this.week ? [...this.week.Airings] : null;
        await this.run(
            (channelId) => deleteAiring(channelId, airingId),
            () => {
                if (!this.week) { return; }
                this.week.Airings = this.week.Airings.filter((a) => a.Id !== airingId);
                if (this.selectedId === airingId) { this.selectedId = null; }
            },
            () => {
                if (this.week && before) { this.week.Airings = before; }
            },
        );
    }

    /** Moves or adds one airing. */
    async place(airing: Partial<WeekAiring>): Promise<void> {
        const before = this.week ? [...this.week.Airings] : null;
        await this.run(
            (channelId) => putAiring(channelId, airing),
            () => {
                if (!this.week || !airing.Id) { return; }
                const at = this.week.Airings.findIndex((a) => a.Id === airing.Id);
                if (at !== -1) {
                    this.week.Airings[at] = { ...this.week.Airings[at], ...airing } as WeekAiring;
                }
            },
            () => {
                if (this.week && before) { this.week.Airings = before; }
            },
        );
    }
}

export const week = new WeekStore();
