<script lang="ts">
    /*
        The blocks, as a list.

        Twice now the owner has said the week view is not wanted here: first that editing a block
        needed the whole week with its times, and then - after it had been shrunk to a 150px
        mini-week - that the section STILL shows its own week view. So it is a list. A block is a
        name, some days and a stretch of hours; a row says exactly that, and the fields for the
        row you have picked sit underneath.
    */
    import { measure, measureWeeklySelection } from '../runtime';
    import SourceList from './SourceList.svelte';
    import SourceSearch from './SourceSearch.svelte';
    import type { ProgramBlock, TvChannel } from '../types';
    import { store } from '../config.svelte';

    let { channel }: { channel: TvChannel } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
    const HEADS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
    let picked = $state(0);
    let collapsed = $state(false);

    const blocks = $derived(channel.Blocks ?? []);
    const current = $derived<ProgramBlock | null>(collapsed ? null : (blocks[picked] ?? null));

    /** How long a block runs, in the coarsest unit that is still true. */
    function spanWords(minutes: number): string {
        if (minutes % 60 === 0) {
            const hours = minutes / 60;
            return hours + (hours === 1 ? ' hour' : ' hours');
        }
        if (minutes < 60) { return minutes + ' min'; }
        return Math.floor(minutes / 60) + ' h ' + (minutes % 60) + ' min';
    }

    /** Which days, said the way a person would say it. */
    function daysWords(block: ProgramBlock): string {
        const on = DAYS.filter((d) => (block.Days ?? []).includes(d));
        if (on.length === 7) { return 'every day'; }
        if (on.length === 0) { return 'no days - this block never runs'; }
        if (on.length === 5 && !on.includes('Saturday') && !on.includes('Sunday')) { return 'weekdays'; }
        if (on.length === 2 && on.includes('Saturday') && on.includes('Sunday')) { return 'weekends'; }
        return on.map((d) => HEADS[DAYS.indexOf(d)]).join(' ');
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
            Name: german ? 'Neuer Block' : 'New block',
            Enabled: true,
            StartMinutes: 20 * 60,
            DurationMinutes: 180,
            Days: [...DAYS],
            Sources: [],
            EpisodesPerBlock: 0,
            Order: 'Sequential',
            RandomizeEpisodes: false,
            SameSourceProbability: 20,
            AdvanceOnePerWeek: false,
            FitToContent: true,
            ShiftToAvoidLeadingGap: false,
            TrailerEnabled: false,
            TrailerProgramsBefore: 3,
        });
        picked = channel.Blocks.length - 1;
        collapsed = false;
    }

    function removeBlock(): void {
        if (!current) { return; }
        channel.Blocks.splice(picked, 1);
        picked = Math.max(0, picked - 1);
    }

    function toggleDay(day: string): void {
        if (!current) { return; }
        const at = current.Days.indexOf(day);
        if (at === -1) { current.Days.push(day); } else { current.Days.splice(at, 1); }
    }

    let fitting = $state(false);
    let account = $state<string | null>(null);
    let currentFilmMinutes = $state<Record<number, number>>({});
    let durationRequest = 0;

    function currentWeeklyOccurrence(block: ProgramBlock): number | null {
        if (!block.AdvanceOnePerWeek || block.Sources.length === 0) { return null; }

        const anchor = new Date(channel.AnchorUtc || '1970-01-05T00:00:00Z');
        const today = new Date();
        const anchorDate = new Date(anchor.getFullYear(), anchor.getMonth(), anchor.getDate());
        const todayDate = new Date(today.getFullYear(), today.getMonth(), today.getDate());
        const daysFromAnchor = Math.floor((todayDate.getTime() - anchorDate.getTime()) / 86400000);
        const week = Math.floor(daysFromAnchor / 7);
        const dayNames = [...DAYS];
        const activeDays = block.Days.length > 0 ? block.Days : dayNames;
        const todayName = dayNames[(todayDate.getDay() + 6) % 7];
        const todayIndex = dayNames.indexOf(todayName);
        const startsToday = today.getHours() * 60 + today.getMinutes() >= block.StartMinutes;
        const ordinal = activeDays.filter((day) => {
            const index = dayNames.indexOf(day);
            return index < todayIndex || (index === todayIndex && startsToday);
        }).length;
        return Math.max(0, week) * activeDays.length + Math.max(0, ordinal - 1);
    }

    async function refreshCurrentFilmDurations(): Promise<void> {
        const request = ++durationRequest;
        const next: Record<number, number> = {};
        await Promise.all(blocks.map(async (block, index) => {
            if (!block.FitToContent) { return; }
            const occurrence = currentWeeklyOccurrence(block);
            if (occurrence === null) { return; }
            try {
                const measured = await measureWeeklySelection(block.Sources, occurrence);
                if (measured.minutes > 0) {
                    next[index] = measured.minutes;
                    account = measured.account;
                }
            } catch { /* The row keeps its honest loading label. */ }
        }));
        if (request === durationRequest) { currentFilmMinutes = next; }
    }

    $effect(() => {
        void channel.Id;
        void JSON.stringify(blocks.map((block) => [
            block.AdvanceOnePerWeek, block.FitToContent, block.StartMinutes,
            block.Days, block.Sources.map((source) => source.ItemId),
        ]));
        void refreshCurrentFilmDurations();
    });

    function summaryMinutes(block: ProgramBlock, index: number): number | null {
        if (block.AdvanceOnePerWeek && block.FitToContent) {
            return currentFilmMinutes[index] ?? null;
        }
        return block.DurationMinutes;
    }

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
    }
</script>

{#if blocks.length > 0}
<div class="blocks">
    {#each blocks as block, index (index)}
        {@const minutes = summaryMinutes(block, index)}
        <button
            type="button"
            class="block-row"
            class:picked={index === picked}
            onclick={() => { picked = index; collapsed = false; }}
        >
            <span class="swatch" style="background: {fillOf(index)}"></span>
            <span class="block-name" title={block.Name}>{block.Name}</span>
            <span class="when">
                {clock(block.StartMinutes)}&ndash;{minutes === null
                    ? (german ? 'Filmlänge' : 'film length')
                    : clock((block.StartMinutes + minutes) % (24 * 60))}
            </span>
            <span class="how-long">{minutes === null
                ? (german ? 'wird ermittelt…' : 'measuring…')
                : spanWords(minutes)}</span>
            <span class="on-days">{daysWords(block)}</span>
            {#if !block.Enabled}<span class="off">{german ? 'aus' : 'off'}</span>{/if}
        </button>
    {/each}
</div>
{/if}

<div class="editor">
    {#if current}
        <div class="line">
            <input class="name" bind:value={current.Name} aria-label="Block name" />
            <label>
                {german ? 'Start' : 'Starts'}
                <input
                    type="time"
                    value={clock(current.StartMinutes)}
                    onchange={(event) => setStart(event.currentTarget.value)}
                />
            </label>
            <label>
                {german ? 'Dauer' : 'For'}
                <input type="number" min="15" step="15" bind:value={current.DurationMinutes} disabled={current.FitToContent !== false} />
                {german ? 'Min.' : 'min'}
            </label>
            <button type="button" class="ghost" onclick={fitToContent} disabled={fitting}>
                {fitting ? (german ? 'Wird gemessen…' : 'Measuring…') : (german ? 'An Inhalt anpassen' : 'Fit to content')}
            </button>
            <button type="button" class="ghost" onclick={addBlock}>{german ? 'Neuer Block' : 'New block'}</button>
            <button type="button" class="ghost" onclick={() => (collapsed = true)}>{german ? 'Einklappen' : 'Collapse'}</button>
            <button type="button" class="ghost danger" onclick={removeBlock}>{german ? 'Löschen' : 'Delete'}</button>
        </div>

        <label class="weekly-film">
            <input
                type="checkbox"
                checked={current.FitToContent !== false}
                onchange={(event) => (current.FitToContent = event.currentTarget.checked)}
            />
            <span>
                <strong>{german ? 'Block an Inhalt anpassen' : 'Fit block to content'}</strong>
                <small>
                    {german
                        ? 'Standardmäßig aktiv. Beim Filmabend wird die Länge des Films der jeweiligen Woche verwendet.'
                        : 'On by default. For film night, the length of that week\'s film is used.'}
                </small>
            </span>
        </label>

        {#if account}<p class="account">{account}</p>{/if}

        <div class="days">
            {#each DAYS as day, i (day)}
                <button type="button" class="day-chip" class:on={onDay(current, day)} onclick={() => toggleDay(day)}>
                    {HEADS[i]}
                </button>
            {/each}
        </div>

        <label class="weekly-film">
            <input type="checkbox" bind:checked={current.AdvanceOnePerWeek} />
            <span>
                <strong>{german ? 'Filmabend: ein Film pro Woche' : 'Film night: one film per week'}</strong>
                <small>
                    {german
                        ? 'Die ausgewählten Filme werden der Reihe nach abgespielt: erster Film diese Woche, nächster Film nächste Woche.'
                        : 'Play the selected films in order: the first this week, the next one next week.'}
                </small>
            </span>
        </label>

        {#if current.AdvanceOnePerWeek}
            <label class="weekly-film">
                <input type="checkbox" bind:checked={current.ShiftToAvoidLeadingGap} />
                <span>
                    <strong>{german ? 'Filmstart an Programmende anpassen' : 'Move film start to a programme boundary'}</strong>
                    <small>{german
                        ? 'Der Film darf sich um die Wunschzeit verschieben, damit davor keine angebrochene Folge oder leere Pause entsteht. Seine tatsächliche Länge verschiebt das Ende entsprechend mit.'
                        : 'The film may move around its preferred time so there is no cut episode or empty lead-in. Its actual running time moves the end with it.'}</small>
                </span>
            </label>
        {/if}

        <label class="weekly-film">
            <input type="checkbox" bind:checked={current.TrailerEnabled} />
            <span>
                <strong>{german ? 'Trailer für den heutigen Block-Film' : 'Trailer for today\'s block film'}</strong>
                <small>
                    {german
                        ? 'Nur der Trailer des Films, der in diesem Block läuft. Der Trailer erscheint so viele Programme vorher wie eingestellt.'
                        : 'Only the trailer for the film airing in this block. The trailer appears the configured number of programmes ahead.'}
                </small>
            </span>
        </label>
        {#if current.TrailerEnabled}
            <label class="trailer-count">
                {german ? 'Programme vorher' : 'Programmes before'}
                <input type="number" min="1" step="1" bind:value={current.TrailerProgramsBefore} />
            </label>
        {/if}

        <div class="block-content">
            <div class="sub">{german ? 'Filme für diesen Block auswählen' : 'Choose films for this block'}</div>
            <div class="inset">
                <SourceList sources={current.Sources} empty="Nothing yet — this block would play the channel's own list." />
                <SourceSearch sources={current.Sources} />
            </div>
        </div>
    {:else}
        <div class="line">
            <!-- The only empty state. The list above used to carry one as well, so with no
                 blocks the same sentence was printed twice, once with a button and once
                 without. -->
            <span class="empty">
                {blocks.length > 0
                    ? (german ? 'Block eingeklappt.' : 'Block collapsed.')
                    : (german ? 'Keine Blöcke — die ganze Woche spielt die Liste oben.' : 'No blocks — the whole week plays the list above.')}
            </span>
            <button type="button" class="ghost" onclick={addBlock}>{german ? 'Neuer Block' : 'New block'}</button>
        </div>
    {/if}
</div>

<style>
    .blocks {
        display: flex;
        flex-direction: column;
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        overflow: hidden;
    }

    .block-row {
        display: flex;
        align-items: center;
        gap: 11px;
        padding: 9px 12px;
        background: none;
        border: none;
        border-bottom: 1px solid var(--lt-line-soft);
        font-family: inherit;
        font-size: 12.5px;
        color: var(--lt-text-muted);
        text-align: left;
        cursor: pointer;
        min-width: 0;
    }

    .block-row:last-child { border-bottom: none; }
    .block-row:hover { background: var(--lt-hover); }
    .block-row.picked { background: var(--lt-accent-soft); box-shadow: inset 2px 0 0 var(--lt-accent); }

    .swatch { flex: 0 0 4px; align-self: stretch; border-radius: 2px; }

    .block-name {
        flex: 1 1 0;
        min-width: 0;
        font-weight: 600;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .when { flex: 0 0 auto; font-variant-numeric: tabular-nums; color: var(--lt-text); }
    .how-long { flex: 0 0 auto; }
    .on-days { flex: 0 0 auto; color: var(--lt-text-dim); }

    .off {
        flex: 0 0 auto;
        font-size: 10.5px;
        text-transform: uppercase;
        letter-spacing: .06em;
        color: var(--lt-collection);
    }


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

    .weekly-film {
        display: flex;
        align-items: flex-start;
        gap: 9px;
        margin-top: 12px;
        padding: 10px 12px;
        border: 1px solid rgba(119, 91, 244, .25);
        border-radius: var(--lt-radius-small);
        background: rgba(119, 91, 244, .07);
        cursor: pointer;
    }

    .weekly-film input { flex: 0 0 auto; margin: 2px 0 0; }
    .weekly-film strong { display: block; font-size: 12.5px; color: var(--lt-text-body); }
    .weekly-film small { display: block; margin-top: 3px; font-size: 11.5px; line-height: 1.4; color: var(--lt-text-muted); }

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
