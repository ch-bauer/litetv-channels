<script lang="ts">
    /*
        The week, drawn.

        Five things the owner asked for live here:

         - **A selection can be cleared.** Clicking the selected bar again, clicking the empty
           grid, or Escape. The old page could only ever move the selection to something else.
         - **The now line**, and the grid **opens scrolled to it** rather than at the top of the
           day. Arriving at midnight to look at a channel that is on air now was absurd.
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

    const pxPerSecond = $derived(week.zoom / 3600);
    const dayHeight = $derived(SECONDS_PER_DAY * pxPerSecond);

    /** Which days are drawn: all seven, or just the one the day view is on. */
    const days = $derived(week.view === 'week' ? [0, 1, 2, 3, 4, 5, 6] : [dayOf(now)]);

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
        const target = secondOfDay(now) * pxPerSecond - body.clientHeight / 3;
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
            if (selected.Id) { void week.remove(selected.Id); }
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

    function onDrop(event: DragEvent, day: number): void {
        event.preventDefault();
        const payload = event.dataTransfer?.getData('text/plain');
        if (!payload || !onDropItem) { return; }
        const column = event.currentTarget as HTMLElement;
        const y = event.clientY - column.getBoundingClientRect().top;
        let second = day * SECONDS_PER_DAY + Math.max(0, y / pxPerSecond);
        // Alt drops on the second; otherwise it lands on the nearest five minutes, which is
        // what anyone actually means by dropping a programme "at eight".
        if (!event.altKey) { second = Math.round(second / 300) * 300; }
        onDropItem(payload, Math.round(second));
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
            <div class="head" class:today={day === dayOf(nowSecond())}>{DAY_SHORT[day]}</div>
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
                ondragover={(e) => e.preventDefault()}
                ondrop={(e) => onDrop(e, day)}
            >
                {#each barsFor(day) as airing (airing.Id)}
                    {@const height = heightOf(airing, day)}
                    <button
                        type="button"
                        class="bar"
                        class:selected={week.selectedId === airing.Id}
                        style="top: {topOf(airing, day)}px; height: {height}px; background: {KIND_FILL[airing.Kind]};"
                        title="{airing.Name} — {clock(secondOfDay(airing.StartSecond))}, {Math.round(airing.DurationSeconds / 60)} min"
                        onclick={(e) => pick(airing, e)}
                    >
                        {#if labelled(height)}{airing.Name}{/if}
                    </button>
                {/each}

                {#if dayOf(now) === day}
                    <div class="now" style="top: {secondOfDay(now) * pxPerSecond}px">
                        <span class="now-time">{clock(secondOfDay(now))}</span>
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
    }

    .bar:hover { filter: brightness(1.12); }

    /* A ring, never a size change: a bar that grows on focus shoves its neighbours. */
    .bar.selected { box-shadow: 0 0 0 2px #fff; }

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
