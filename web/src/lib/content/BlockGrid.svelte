<script lang="ts">
    /*
        The blocks, drawn as what they are: shapes on a week.

        The owner's complaint was that editing a block needed the whole week view with its times.
        It does not - a block is a stretch of hours repeated on some days, and this grid gives a
        whole day in 150px, which is enough to see the shape and pick one. The fields for the
        block you have picked sit underneath, where they belong.
    */
    import { store } from '../config.svelte';
    import { measure } from '../runtime';
    import SourceList from './SourceList.svelte';
    import SourceSearch from './SourceSearch.svelte';
    import type { ProgramBlock, TvChannel } from '../types';

    let { channel }: { channel: TvChannel } = $props();

    const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
    const HEADS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
    const TICKS = [
        { label: '00', top: 0 }, { label: '06', top: 37 }, { label: '12', top: 74 },
        { label: '18', top: 111 }, { label: '24', top: 142 },
    ];
    const DAY_HEIGHT = 150;

    let picked = $state(0);

    const blocks = $derived(channel.Blocks ?? []);
    const current = $derived<ProgramBlock | null>(blocks[picked] ?? null);

    function shapeOf(block: ProgramBlock): { top: number; height: number } {
        const perMinute = DAY_HEIGHT / (24 * 60);
        const top = Math.round(block.StartMinutes * perMinute);
        // A block running past midnight is drawn to the end of the day rather than off it;
        // what it does after midnight is the schedule's business, not this picture's.
        const span = Math.min(block.DurationMinutes, 24 * 60 - block.StartMinutes);
        return { top, height: Math.max(3, Math.round(span * perMinute)) };
    }

    function onDay(block: ProgramBlock, day: string): boolean {
        return (block.Days ?? []).includes(day);
    }

    function fillOf(index: number): string {
        const palette = [
            'rgba(119,91,244,.8)', 'rgba(217,154,58,.75)',
            'rgba(91,110,225,.75)', 'rgba(90,160,120,.75)',
        ];
        return palette[index % palette.length];
    }

    function clock(minutes: number): string {
        const h = Math.floor(minutes / 60) % 24;
        const m = minutes % 60;
        return String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
    }

    function addBlock(): void {
        channel.Blocks.push({
            Name: 'New block',
            Enabled: true,
            StartMinutes: 20 * 60,
            DurationMinutes: 180,
            Days: [...DAYS],
            Sources: [],
            EpisodesPerBlock: 0,
            Order: 'Sequential',
        });
        picked = channel.Blocks.length - 1;
        store.touch();
    }

    function removeBlock(): void {
        if (!current) { return; }
        channel.Blocks.splice(picked, 1);
        picked = Math.max(0, picked - 1);
        store.touch();
    }

    function toggleDay(day: string): void {
        if (!current) { return; }
        const at = current.Days.indexOf(day);
        if (at === -1) { current.Days.push(day); } else { current.Days.splice(at, 1); }
        store.touch();
    }

    let fitting = $state(false);
    let account = $state<string | null>(null);

    /*
        Item 14: a block's length comes from what it plays, not from a typed number. The server
        really does read DurationMinutes - ChannelPlaylistBuilder and WeekTimeline both use it -
        so setting it here is honest, which is the check every numeric control on this page now
        has to pass.
    */
    async function fitToContent(): Promise<void> {
        if (!current) { return; }
        fitting = true;
        try {
            const measured = await measure(current.Sources, current.EpisodesPerBlock);
            account = measured.account;
            if (measured.minutes > 0) {
                current.DurationMinutes = measured.minutes;
                store.touch();
            }
        } finally {
            fitting = false;
        }
    }

    function setStart(value: string): void {
        if (!current) { return; }
        const [h, m] = value.split(':').map(Number);
        if (Number.isNaN(h) || Number.isNaN(m)) { return; }
        current.StartMinutes = h * 60 + m;
        store.touch();
    }
</script>

<div class="grid">
    <div class="heads">
        <div class="gutter"></div>
        {#each HEADS as head (head)}<div class="head">{head}</div>{/each}
    </div>

    <div class="body">
        <div class="gutter ticks">
            {#each TICKS as tick (tick.label)}<span style="top: {tick.top}px">{tick.label}</span>{/each}
        </div>

        {#each DAYS as day (day)}
            <div class="day">
                {#each blocks as block, index (index)}
                    {#if onDay(block, day)}
                        {@const shape = shapeOf(block)}
                        <button
                            type="button"
                            class="block"
                            class:picked={index === picked}
                            style="top: {shape.top}px; height: {shape.height}px; background: {fillOf(index)};"
                            title="{block.Name} - {clock(block.StartMinutes)} for {Math.round(block.DurationMinutes / 60)} h"
                            onclick={() => (picked = index)}
                        >{block.Name}</button>
                    {/if}
                {/each}
            </div>
        {/each}
    </div>
</div>

<div class="editor">
    {#if current}
        <div class="line">
            <input class="name" bind:value={current.Name} oninput={() => store.touch()} aria-label="Block name" />
            <label>
                Starts
                <input
                    type="time"
                    value={clock(current.StartMinutes)}
                    onchange={(event) => setStart(event.currentTarget.value)}
                />
            </label>
            <label>
                For
                <input type="number" min="15" step="15" bind:value={current.DurationMinutes} oninput={() => store.touch()} />
                min
            </label>
            <button type="button" class="ghost" onclick={fitToContent} disabled={fitting}>
                {fitting ? 'Measuring…' : 'Fit to content'}
            </button>
            <button type="button" class="ghost" onclick={addBlock}>New block</button>
            <button type="button" class="ghost danger" onclick={removeBlock}>Delete</button>
        </div>

        {#if account}<p class="account">{account}</p>{/if}

        <div class="days">
            {#each DAYS as day, i (day)}
                <button type="button" class="day-chip" class:on={onDay(current, day)} onclick={() => toggleDay(day)}>
                    {HEADS[i]}
                </button>
            {/each}
        </div>

        <div class="block-content">
            <div class="sub">What this block plays instead</div>
            <div class="inset">
                <SourceList sources={current.Sources} empty="Nothing yet — this block would play the channel's own list." />
                <SourceSearch sources={current.Sources} />
            </div>
        </div>
    {:else}
        <div class="line">
            <span class="empty">No blocks - the whole week plays the list above.</span>
            <button type="button" class="ghost" onclick={addBlock}>New block</button>
        </div>
    {/if}
</div>

<style>
    .grid {
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        padding: 11px 13px;
    }

    .heads, .body { display: flex; gap: 6px; }
    .heads { margin-bottom: 5px; }

    .gutter { flex: 0 0 34px; position: relative; }

    .head {
        flex: 1 1 0;
        text-align: center;
        font-size: 10.5px;
        font-weight: 600;
        color: var(--lt-text-dim);
    }

    .ticks { height: 150px; }

    .ticks span {
        position: absolute;
        right: 4px;
        font-size: 9.5px;
        color: var(--lt-text-faint);
    }

    .day {
        flex: 1 1 0;
        position: relative;
        height: 150px;
        border-radius: 3px;
        background: repeating-linear-gradient(to bottom, rgba(255, 255, 255, .05) 0 1px, transparent 1px 25px);
    }

    .block {
        position: absolute;
        left: 1px;
        right: 1px;
        border: none;
        border-radius: 3px;
        font-size: 9.5px;
        font-family: inherit;
        color: #fff;
        padding: 2px 4px;
        overflow: hidden;
        text-align: left;
        cursor: pointer;
    }

    .block:hover { filter: brightness(1.12); }
    .block.picked { box-shadow: 0 0 0 2px #fff; }

    .editor { margin-top: 10px; }

    .line {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
        font-size: 12.5px;
        color: var(--lt-text-muted);
    }

    .line label { display: inline-flex; align-items: center; gap: 6px; }

    input {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 6px 9px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text);
    }

    input.name { flex: 1 1 10em; min-width: 8em; font-weight: 600; }
    input[type='number'] { width: 5.5em; }

    .ghost {
        background: rgba(255, 255, 255, .05);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 6px 11px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text-body);
        cursor: pointer;
    }

    .ghost:hover { background: var(--lt-hover); }
    .ghost.danger:hover { color: #e08585; border-color: rgba(224, 133, 133, .4); }

    .days { display: flex; gap: 5px; margin-top: 9px; }

    .day-chip {
        flex: 0 0 auto;
        padding: 4px 10px;
        border-radius: 999px;
        border: 1px solid var(--lt-line-strong);
        background: none;
        font-size: 11px;
        font-family: inherit;
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .day-chip.on {
        background: var(--lt-accent);
        border-color: var(--lt-accent);
        color: #fff;
    }

    .empty { color: var(--lt-text-dim); }

    .account {
        margin: 9px 0 0;
        font-size: 12px;
        color: var(--lt-text-muted);
        padding-left: 13px;
        border-left: 2px solid var(--lt-accent);
    }

    .block-content { margin-top: 12px; }

    .sub {
        font-size: 12.5px;
        font-weight: 600;
        color: var(--lt-text-body);
        margin-bottom: 7px;
    }

    .inset {
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        overflow: hidden;
    }

    .ghost:disabled { opacity: .55; cursor: default; }
</style>
