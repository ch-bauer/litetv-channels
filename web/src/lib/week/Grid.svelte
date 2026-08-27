<script lang="ts">
    /*
        The week, drawn.

        Five things the owner asked for live here:

         - **A selection can be cleared.** Clicking the selected bar again, clicking the empty
           grid, or Escape. The old page could only ever move the selection to something else.
         - **The now line**, and the grid **opens scrolled to it** rather than at the top of the
           day. Arriving at midnight to look at a channel that is on air now was absurd.
         - **Drag to move.** A bar is dragged to another time or another day, and it lands
           where it looks like it is landing - the grab point is kept, not the bar's top edge.
         - **Keyboard**: Delete takes the selected airing off; the arrows move between airings;
           Alt with an arrow nudges the selected one by five minutes.
         - **It scales.** Day columns are fractions, not fixed widths, and the body scrolls
           inside itself, so the grid works from a narrow dashboard to a very wide one.
         - **Zoom** runs the range the slider offers, and each view remembers its own.
    */
    import { week } from './weekStore.svelte';
    import {
        DAY_SHORT, KIND_FILL, SECONDS_PER_DAY, clock, dayOf, nowSecond, secondOfDay,
        type WeekAiring,
    } from '../api/week';

    let { onDropItem }: { onDropItem?: (payload: string, second: number) => void } = $props();

    let body = $state<HTMLDivElement | null>(null);
    let now = $state(nowSecond());
    let scrolledToNow = false;

    // The clock ticks so the now line is not a lie after ten minutes of looking at it.
    $effect(() => {
        const timer = setInterval(() => (now = nowSecond()), 30000);
        return () => clearInterval(timer);
    });

    /*
        Now, as a second of the CYCLE rather than of this week.

        `nowSecond()` is seconds after this Monday, which is the whole story for a schedule one
        week long and half of it for any other: on a fortnightly channel, Monday evening is
        second 20*3600 of week one or of week two depending which week of the fortnight is
        running, and only the server knows which - it counts from a fixed Monday. So it is
        asked, and this adds the offset.
    */
    const nowInCycle = $derived(week.currentWeek * 7 * SECONDS_PER_DAY + now);

    /** The first day column of the week being looked at, as a day of the whole cycle. */
    const firstDay = $derived(week.weekIndex * 7);

    const pxPerSecond = $derived(week.zoom / 3600);
    const dayHeight = $derived(SECONDS_PER_DAY * pxPerSecond);

    /*
        Which days are drawn: the seven of the week being looked at, or the one the day view is
        on. A schedule of four weeks is twenty-eight columns and nobody can find Thursday in
        that, so the grid draws one week of the cycle at a time.
    */
    const days = $derived.by(() => {
        if (week.view === 'week') {
            return [0, 1, 2, 3, 4, 5, 6].map((d) => firstDay + d);
        }
        // The day view opens on today when today is in the week being looked at, and on its
        // Monday otherwise - there is no "today" in next week.
        return [week.weekIndex === week.currentWeek ? dayOf(nowInCycle) : firstDay];
    });

    const placed = $derived(
        week.airings.filter((a) => a.Kind !== 'Gap' && a.Id !== null),
    );

    function barsFor(day: number): WeekAiring[] {
        const from = day * SECONDS_PER_DAY;
        const to = from + SECONDS_PER_DAY;
        return placed.filter((a) => a.StartSecond < to && a.StartSecond + a.DurationSeconds > from);
    }

    function topOf(airing: WeekAiring, day: number): number {
        return Math.max(0, (airing.StartSecond - day * SECONDS_PER_DAY)) * pxPerSecond;
    }

    function heightOf(airing: WeekAiring, day: number): number {
        const from = Math.max(airing.StartSecond, day * SECONDS_PER_DAY);
        const to = Math.min(airing.StartSecond + airing.DurationSeconds, (day + 1) * SECONDS_PER_DAY);
        return Math.max(2, (to - from) * pxPerSecond);
    }

    /*
        How small a bar may be before its name is dropped. A ten-minute advert at a week's zoom is
        eight pixels tall and a label in it is a smear; zooming in lowers this floor in real
        minutes, which is the whole point of the zoom.
    */
    function labelled(height: number): boolean {
        return height >= 13;
    }

    const hours = $derived.by(() => {
        // Labels thin out as the zoom does, or they collide.
        const step = week.zoom >= 260 ? 1 : week.zoom >= 90 ? 2 : week.zoom >= 40 ? 3 : 6;
        const out: { label: string; top: number }[] = [];
        for (let h = 0; h <= 24; h += step) {
            out.push({ label: String(h).padStart(2, '0'), top: h * week.zoom });
        }
        return out;
    });

    function scrollToNow(): void {
        if (!body) { return; }
        // The evening, wherever the grid is: a week of the cycle that is not the current one
        // has no "now" in it, and opening it at midnight is the absurdity the now-scroll was
        // written to fix in the first place.
        const at = week.weekIndex === week.currentWeek ? secondOfDay(nowInCycle) : 19 * 3600;
        const target = at * pxPerSecond - body.clientHeight / 3;
        body.scrollTop = Math.max(0, target);
    }

    // Once, when the grid first has a size. Doing it on every draw would fight the scrollbar
    // every time a bar moved.
    $effect(() => {
        if (body && !scrolledToNow && dayHeight > 0) {
            scrolledToNow = true;
            scrollToNow();
        }
    });

    function pick(airing: WeekAiring, event: MouseEvent): void {
        event.stopPropagation();
        week.toggle(airing.Id);
    }

    function onGridKey(event: KeyboardEvent): void {
        if (event.key === 'Escape') { week.selectedId = null; return; }

        const selected = week.selected;
        if (!selected) { return; }

        if (event.key === 'Delete' || event.key === 'Backspace') {
            event.preventDefault();
            if (!selected.Id) { return; }

            /*
                Taking one off moves the selection to what follows it, so a run of them can be
                cleared with the key held down. Leaving nothing selected meant reaching for the
                mouse between every deletion, which is what the owner objected to. The last one
                in the week hands the selection back to the one before it.
            */
            const order = placed.slice().sort((a, b) => a.StartSecond - b.StartSecond);
            const at = order.findIndex((a) => a.Id === selected.Id);
            const next = order[at + 1] ?? order[at - 1] ?? null;

            const going = selected.Id;
            void week.remove(going).then(() => {
                // Only if nothing else has claimed the selection in the meantime.
                if (week.selectedId === null && next && next.Id !== going) {
                    week.selectedId = next.Id;
                }
            });
            return;
        }

        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            event.preventDefault();
            const step = event.key === 'ArrowUp' ? -1 : 1;
            if (event.altKey) {
                // Five minutes a press: fine enough to fix a start time, coarse enough that
                // holding the key does something visible.
                void week.place({ ...selected, StartSecond: Math.max(0, selected.StartSecond + step * 300) });
            } else {
                const order = placed.slice().sort((a, b) => a.StartSecond - b.StartSecond);
                const at = order.findIndex((a) => a.Id === selected.Id);
                const next = order[at + step];
                if (next) { week.selectedId = next.Id; }
            }
        }
    }

    /*
        SNAPPING.

        There was snapping before - the drop rounded to the nearest five minutes - and the
        owner's report was that there is none. Both are true: five minutes at a week's zoom is
        less than a pixel, so nothing about it could be seen, and rounding to an arbitrary
        five-minute mark is not what anyone means by snapping a programme. What they mean is
        that it lands FLUSH: against the end of the programme before it, the start of the one
        after, or a round time on the clock.

        So the targets are the edges that exist - every row's start and end - and the quarter
        hours; the nearest one wins if it is within grabbing distance ON SCREEN rather than in
        seconds, so zooming in makes snapping finer, which is what zooming is for; and the drop
        line is DRAWN while the drag is in the air, with what it snapped to written beside it.
        Holding Alt turns the whole thing off and drops on the second.
    */
    const SNAP_PIXELS = 9;
    const QUARTER = 15 * 60;

    /** Where the drop would land: drawn as a line, and used when the drop happens. */
    let preview = $state<{ day: number; second: number; snapped: string | null } | null>(null);

    /** How long the thing being dragged runs, so its END can snap too. Zero when unknown. */
    let draggingSeconds = 0;

    /** The edges a drop can land on: every row's start and end, and every quarter hour. */
    function snapTargets(): number[] {
        const out: number[] = [];
        for (const airing of placed) {
            out.push(airing.StartSecond, airing.StartSecond + airing.DurationSeconds);
        }
        for (let t = 0; t <= week.weeks * 7 * SECONDS_PER_DAY; t += QUARTER) { out.push(t); }
        return out;
    }

    /**
     * Where a drop at this position lands, and what it landed on.
     *
     * The dragged row's own end is offered as well as its start, so a programme can be laid
     * flush BEFORE the next one as easily as after the last.
     */
    function snap(raw: number, free: boolean): { second: number; snapped: string | null } {
        if (free) { return { second: Math.max(0, Math.round(raw)), snapped: null }; }

        const within = SNAP_PIXELS / pxPerSecond;
        let best: { second: number; distance: number; edge: 'start' | 'end' } | null = null;

        for (const target of snapTargets()) {
            for (const edge of ['start', 'end'] as const) {
                if (edge === 'end' && draggingSeconds <= 0) { continue; }
                const candidate = edge === 'start' ? target : target - draggingSeconds;
                const distance = Math.abs(candidate - raw);
                if (distance <= within && (best === null || distance < best.distance)) {
                    best = { second: candidate, distance, edge };
                }
            }
        }

        if (best) {
            return {
                second: Math.max(0, Math.round(best.second)),
                snapped: best.edge === 'end' ? 'ends flush' : 'starts flush',
            };
        }

        // Nothing near. The nearest five minutes, which is still what anyone means by
        // dropping a programme "at eight".
        return { second: Math.max(0, Math.round(raw / 300) * 300), snapped: null };
    }

    /** The second under the pointer, allowing for where in the bar it was picked up. */
    function secondAt(event: DragEvent, day: number): number {
        const column = event.currentTarget as HTMLElement;
        const y = event.clientY - column.getBoundingClientRect().top - grabbedAt;
        return day * SECONDS_PER_DAY + Math.max(0, y / pxPerSecond);
    }

    function onDragOver(event: DragEvent, day: number): void {
        event.preventDefault();
        const landed = snap(secondAt(event, day), event.altKey);
        preview = { day, second: landed.second, snapped: landed.snapped };
    }

    /*
        Two things can be dragged onto a day, and they are told apart by what the drag carries:
        an entry from the shelf, which the screen turns into a new airing, and a bar already on
        the week, which is a move. A move is done here because only the grid knows the airing.
    */
    function onDrop(event: DragEvent, day: number): void {
        event.preventDefault();
        const payload = event.dataTransfer?.getData('text/plain');
        preview = null;
        if (!payload) { return; }

        const second = snap(secondAt(event, day), event.altKey).second;

        let moved: WeekAiring | null = null;
        try {
            const parsed = JSON.parse(payload) as { airingId?: string };
            if (parsed.airingId) {
                moved = placed.find((a) => a.Id === parsed.airingId) ?? null;
            }
        } catch {
            // Not ours. The screen decides what to do with it.
        }

        if (moved) {
            // Everything the row already is, at a new time: the server keeps its length, its
            // item and its offset, and bends the rest of the week around it.
            void week.place({ ...moved, StartSecond: second });
            return;
        }

        onDropItem?.(payload, second);
    }

    /*
        Where in the bar it was picked up, so a programme dropped lands where it looks like it
        is being dropped rather than jumping its own height up the day.
    */
    let grabbedAt = 0;

    function onBarDragStart(event: DragEvent, airing: WeekAiring): void {
        const bar = event.currentTarget as HTMLElement;
        grabbedAt = event.clientY - bar.getBoundingClientRect().top;
        draggingSeconds = airing.DurationSeconds;
        event.dataTransfer?.setData('text/plain', JSON.stringify({ airingId: airing.Id }));
        if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; }
        week.selectedId = airing.Id;
    }

    /*
        A drag that began outside the grid - an entry from the shelf. Its length is not known
        here, so only its start can snap; the grab point is zero because the pointer is the
        thing being positioned.
    */
    function onOutsideDragEnter(): void {
        // A bar's own drag clears both when it ends, so anything still set here belongs to the
        // drag in progress. A shelf entry has neither: the pointer is the thing being placed,
        // and its length is the server's to work out, so only its start can snap.
        if (draggingSeconds === 0) { grabbedAt = 0; }
    }
</script>

<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<div
    class="grid"
    role="grid"
    tabindex="0"
    aria-label="The week"
    onkeydown={onGridKey}
    onclick={() => (week.selectedId = null)}
>
    <div class="heads">
        <div class="gutter"></div>
        {#each days as day (day)}
            <div class="head" class:today={day === dayOf(nowInCycle)}>{DAY_SHORT[day % 7]}</div>
        {/each}
    </div>

    <div class="body" bind:this={body}>
        <div class="gutter ticks" style="height: {dayHeight}px">
            {#each hours as hour (hour.label + hour.top)}
                <span style="top: {hour.top}px">{hour.label}</span>
            {/each}
        </div>

        {#each days as day (day)}
            <div
                class="day"
                style="height: {dayHeight}px"
                role="gridcell"
                tabindex="-1"
                ondragover={(e) => onDragOver(e, day)}
                ondragenter={onOutsideDragEnter}
                ondragleave={() => (preview = null)}
                ondrop={(e) => onDrop(e, day)}
            >
                {#each barsFor(day) as airing (airing.Id)}
                    {@const height = heightOf(airing, day)}
                    <button
                        type="button"
                        class="bar"
                        class:selected={week.selectedId === airing.Id}
                        draggable="true"
                        ondragstart={(e) => onBarDragStart(e, airing)}
                        ondragend={() => { grabbedAt = 0; draggingSeconds = 0; preview = null; }}
                        style="top: {topOf(airing, day)}px; height: {height}px; background: {KIND_FILL[airing.Kind]};"
                        title="{airing.Name} — {clock(secondOfDay(airing.StartSecond))}, {Math.round(airing.DurationSeconds / 60)} min · drag to move it"
                        onclick={(e) => pick(airing, e)}
                    >
                        {#if labelled(height)}{airing.Name}{/if}
                    </button>
                {/each}

                {#if preview && preview.day === day}
                    <!--
                        The drop line. Snapping that cannot be seen is snapping nobody believes
                        in, which is precisely how five-minute rounding was reported as "no
                        snapping at all".
                    -->
                    <div class="drop" style="top: {secondOfDay(preview.second) * pxPerSecond}px">
                        <span class="drop-time">
                            {clock(secondOfDay(preview.second))}{preview.snapped ? ' · ' + preview.snapped : ''}
                        </span>
                    </div>
                {/if}

                {#if dayOf(nowInCycle) === day}
                    <div class="now" style="top: {secondOfDay(nowInCycle) * pxPerSecond}px">
                        <span class="now-time">{clock(secondOfDay(nowInCycle))}</span>
                    </div>
                {/if}
            </div>
        {/each}
    </div>
</div>

<style>
    .grid {
        flex-grow: 1;
        min-height: 0;
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        display: flex;
        flex-direction: column;
        overflow: hidden;
    }

    .grid:focus-visible { outline-offset: -2px; }

    .heads { display: flex; flex: 0 0 auto; }

    .head {
        flex: 1 1 0;
        min-width: 0;
        height: 25px;
        line-height: 25px;
        text-align: center;
        font-size: 11.5px;
        font-weight: 600;
        background: var(--lt-card);
        border-left: 1px solid var(--lt-line-soft);
        color: var(--lt-text-dim);
    }

    .head.today { color: var(--lt-text-strong); }

    .body {
        flex-grow: 1;
        min-height: 0;
        display: flex;
        overflow-y: auto;
        overflow-x: hidden;
    }

    .gutter { flex: 0 0 44px; position: relative; border-right: 1px solid var(--lt-line); }

    .ticks span {
        position: absolute;
        right: 7px;
        font-size: 10.5px;
        color: var(--lt-text-faint);
        transform: translateY(-0.5em);
    }

    .day {
        flex: 1 1 0;
        min-width: 0;
        position: relative;
        border-left: 1px solid var(--lt-line-soft);
    }

    .bar {
        position: absolute;
        left: 3px;
        right: 3px;
        border: none;
        border-radius: 4px;
        padding: 2px 5px;
        font-size: 10.5px;
        font-weight: 500;
        font-family: inherit;
        color: #fff;
        overflow: hidden;
        text-align: left;
        cursor: pointer;
        line-height: 1.2;
        /*
            A line ALL THE WAY ROUND, so a channel that runs programmes back to back reads as
            programmes rather than as one long blob. Every Programme bar is the same colour and
            they abut exactly; at a week's zoom the names are dropped as well, which left
            nothing to tell one from the next.

            A hairline along the top edge was the first attempt and the owner's answer was that
            it is still hard to see where one part ends. So: a full inset outline, a lighter one
            just inside it to lift the edge off the bar below, and a real one-pixel gap at the
            foot - `bottom` rather than height, so nothing has to be subtracted anywhere else.
        */
        box-shadow: inset 0 0 0 1px rgba(0, 0, 0, .55), inset 0 1px 0 1px rgba(255, 255, 255, .1);
    }

    /*
        The gap. Drawn by shrinking the bar rather than by a margin: a bar is absolutely
        positioned from its start time, and a margin would move it off the time it airs at.
    */
    .bar::after {
        content: '';
        position: absolute;
        left: 0;
        right: 0;
        bottom: 0;
        height: 1px;
        background: var(--lt-ground-bottom);
        border-radius: 0 0 4px 4px;
    }

    .bar:hover { filter: brightness(1.12); }

    /*
        A ring, never a size change: a bar that grows on focus shoves its neighbours.
        The ring is drawn INSIDE the bar. An outer ring reaches 2px beyond the box, into
        the bar that starts where this one ends - and that bar, being later in the day, is
        later in the DOM and paints over it. That is why the highlight had a top, a left
        and a right and no bottom.
    */
    .bar.selected { box-shadow: inset 0 0 0 2px #fff; z-index: 1; }

    .drop {
        position: absolute;
        left: 0;
        right: 0;
        height: 0;
        border-top: 2px dashed var(--lt-accent);
        pointer-events: none;
        z-index: 3;
    }

    .drop-time {
        position: absolute;
        left: 2px;
        top: -1.05em;
        font-size: 9.5px;
        font-weight: 700;
        color: #fff;
        background: var(--lt-accent);
        padding: 0 4px;
        border-radius: 2px;
        white-space: nowrap;
    }

    .now {
        position: absolute;
        left: 0;
        right: 0;
        height: 0;
        border-top: 1px solid #e8654a;
        pointer-events: none;
        z-index: 2;
    }

    .now-time {
        position: absolute;
        left: 2px;
        top: -0.85em;
        font-size: 9.5px;
        font-weight: 700;
        color: #e8654a;
        background: var(--lt-ground-bottom);
        padding: 0 3px;
        border-radius: 2px;
    }
</style>
