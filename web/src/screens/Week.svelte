<script lang="ts">
    /*
        The Week and Day views, and the shelf under them. One screen: Day is the same grid at a
        closer zoom showing one column, which is why they share everything.
    */
    import Grid from '../lib/week/Grid.svelte';
    import Shelf from '../lib/week/Shelf.svelte';
    import { week, ZOOM_MIN, ZOOM_MAX } from '../lib/week/weekStore.svelte';
    import { store } from '../lib/config.svelte';
    import { resolveDuration } from '../lib/api/duration';
    import {
        DAY_NAMES, KIND_FILL, MAX_WEEKS, SECONDS_PER_DAY, clock, dayOf, secondOfDay,
    } from '../lib/api/week';
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    $effect(() => {
        /*
            Reloads whenever the channel changes - and whenever a channel the page only just
            made becomes one the server holds, which is the one thing Save does to it. Reading
            `serverHas` here is what makes the second case happen by itself: a new channel shows
            "not saved yet", and the moment Save lands its week arrives without another prompt.
        */
        // A new channel is saved before its first week is generated. Re-run this effect when
        // that background generation finishes, so a temporary 404 cannot remain on screen.
        const serverHasChannel = store.serverHas(channel.Id);
        const generationDone = !store.isScheduleGenerating(channel.Id);
        void week.load(channel.Id, serverHasChannel, serverHasChannel && generationDone);
    });

    week.restoreZoom();

    const selected = $derived(week.selected);

    const status = $derived.by(() => {
        if (!week.week) { return ''; }
        if (!week.week.Curated) {
            return german ? 'Keine Woche erstellt — dieser Kanal spielt seine Inhalte und Einstellungen ab.' : 'No week laid out — this channel airs from its content and settings.';
        }

        /*
            The Day view counts the DAY.

            It used to give the whole schedule's total whichever view you were in - "386
            scheduled" under a single Friday - which answers a question nobody asked while
            looking at one day, and is the number the design board puts a day's own count in
            the place of.
        */
        if (week.view === 'day') {
            const from = week.shownDay * SECONDS_PER_DAY;
            const to = from + SECONDS_PER_DAY;
            const onDay = week.airings.filter((a) => a.Kind !== 'Gap'
                && a.StartSecond < to && a.StartSecond + a.DurationSeconds > from);
            if (onDay.length === 0) { return (german ? 'Nichts geplant · ' : 'Nothing scheduled · ') + dayWords(from); }

            // Clamped to the day, so a programme running over midnight is reported by the part
            // of it that is on this day rather than by where it started or ended.
            const first = Math.min(...onDay.map((a) => Math.max(a.StartSecond, from)));
            const last = Math.max(...onDay.map((a) => Math.min(a.StartSecond + a.DurationSeconds, to)));
            const until = last >= to ? (german ? 'Mitternacht' : 'midnight') : clock(secondOfDay(last));
            return onDay.length + (german ? ' geplant · ' : ' scheduled · ') + dayWords(from)
                + ' ' + clock(secondOfDay(first)) + '–' + until;
        }

        const placed = week.airings.filter((a) => a.Kind !== 'Gap').length;
        const over = week.weeks === 1 ? '' : (german ? ' über ' + week.weeks + ' Wochen' : ' over ' + week.weeks + ' weeks');
        return placed + (german ? ' geplant' : ' scheduled') + over;
    });

    /** "Thursday", or "week 2 · Thursday" when the schedule runs longer than a week. */
    function dayWords(second: number): string {
        const day = dayOf(second);
        const name = DAY_NAMES[day % 7];
        return week.weeks === 1 ? name : (german ? 'Woche ' : 'week ') + (Math.floor(day / 7) + 1) + ' · ' + name;
    }

    function describe(): string {
        if (!selected) { return ''; }
        const day = dayWords(selected.StartSecond);
        const from = clock(secondOfDay(selected.StartSecond));
        const to = clock(secondOfDay(selected.StartSecond + selected.DurationSeconds));
        const minutes = Math.round(selected.DurationSeconds / 60);
        return selected.Kind + ' · ' + day + ' ' + from + '–' + to + ' · ' + minutes + ' min';
    }

    function sameTimeTomorrow(): void {
        if (!selected) { return; }
        void week.place({
            ItemId: selected.ItemId,
            Url: selected.Url,
            Name: selected.Name,
            Kind: selected.Kind,
            DurationSeconds: selected.DurationSeconds,
            StartSecond: selected.StartSecond + 24 * 60 * 60,
        });
    }

    /*
        The server lays a week out from the configuration it HOLDS, so an unsaved change to the
        channel's content would simply not be in it - the week would come back looking as though
        the edit had been ignored. So this saves first, and says so on the button rather than
        writing to the server behind the owner's back.
    */
    /*
        Same rule as laying out by hand: the server lays a week out from the configuration it
        HOLDS, so unsaved content has to reach it first or the length is fitted to the old
        content and the week laid out from it.
    */
    async function fitToContent(): Promise<void> {
        if (store.dirty) {
            await store.save();
            if (store.dirty) { return; }
        }
        await week.fitToContent();
    }

    async function layOut(): Promise<void> {
        if (store.dirty) {
            await store.save();
            // save() reports its own failure. Still dirty means it did not land, and laying the
            // week out now would quietly use the old content.
            if (store.dirty) { return; }
        }
        /*
            A channel saved a moment ago had no week to load, and the endpoints would have
            refused it. Fetch one now - before the edit rather than after it, so the reload
            cannot throw the edit away.
        */
        if (week.unsaved) { await week.load(channel.Id, store.serverHas(channel.Id)); }
        // Pending, like every other schedule edit: it is drawn at once, Undo takes it back, and
        // nothing is written down until Save. Laying a week out used to be irreversible the
        // instant it was pressed, over a week somebody had curated by hand.
        await week.generate();
    }

    /*
        Typing a start time, which the owner asked for beside snapping: dragging is for putting
        something roughly where it goes, and this is for saying exactly.
    */
    function setStartTime(value: string): void {
        if (!selected) { return; }
        const [h, m] = value.split(':').map(Number);
        if (!Number.isFinite(h) || !Number.isFinite(m)) { return; }
        const day = dayOf(selected.StartSecond);
        void week.place({ ...selected, StartSecond: day * SECONDS_PER_DAY + h * 3600 + m * 60 });
    }

    function setDay(value: string): void {
        if (!selected) { return; }
        const day = Number(value);
        if (!Number.isFinite(day)) { return; }
        void week.place({ ...selected, StartSecond: day * SECONDS_PER_DAY + secondOfDay(selected.StartSecond) });
    }

    interface Dropped {
        itemId: string | null;
        url: string | null;
        name: string;
        /** Set when a whole playlist was dragged: its videos, in order. */
        playlist?: { url: string; name: string; seconds: number }[];
    }

    async function onDropItem(payload: string, second: number): Promise<void> {
        let dropped: Dropped;
        try {
            dropped = JSON.parse(payload) as Dropped;
        } catch {
            // Something else was dragged onto the grid. Nothing to do.
            return;
        }

        /*
            A whole playlist. It goes on as its videos, one after another from where it was
            dropped - which is what dragging a playlist onto a Tuesday evening means. Sent as
            one run, so the week is laid out once rather than once per video, and each length is
            the one YouTube gave; a video it did not measure is left at zero for the server to
            work out from the address, exactly as a single dropped link is.
        */
        if (dropped.playlist && dropped.playlist.length > 0) {
            let at = second;
            const run = dropped.playlist.map((video) => {
                const seconds = Math.round(video.seconds);
                const airing = {
                    ItemId: null,
                    Url: video.url,
                    Name: video.name,
                    Kind: 'Programme' as const,
                    StartSecond: at,
                    DurationSeconds: seconds > 0 ? seconds : 0,
                };
                // An unmeasured video still has to leave room for the next one, or every
                // remaining video in the playlist lands on the same second.
                at += seconds > 0 ? seconds : 30 * 60;
                return airing;
            });
            await week.placeMany(run);
            return;
        }

        /*
            Length is nobody's to type - it is the item's own runtime, which the server reads
            from the library, or an address's playable length, which only the resolver knows.
            Zero means "you work it out"; the server no longer stores it as a zero-length row.
        */
        let seconds = 0;
        if (dropped.url) {
            try {
                const measured = await resolveDuration(dropped.url);
                seconds = measured.PlayableSeconds > 0 ? measured.PlayableSeconds : measured.LengthSeconds;
            } catch {
                // The address could not be measured. The server's fallback length applies, and
                // the row can be seen and moved rather than silently not being there.
            }
        }

        await week.place({
            ItemId: dropped.itemId,
            Url: dropped.url ?? '',
            Name: dropped.name,
            Kind: 'Programme',
            StartSecond: second,
            DurationSeconds: Math.round(seconds),
        });
    }
</script>

<!--
    Clicking anywhere that is not a programme lets the selection go. It used to work only on
    the grid itself, which the owner reported as it working "only in the right side of the
    view" - everywhere else, a selected bar stayed selected with no way to say otherwise
    except finding the small button that says so.
-->
<!-- svelte-ignore a11y_no_static_element_interactions -->
<!-- svelte-ignore a11y_click_events_have_key_events -->
<div class="screen" onclick={() => (week.selectedId = null)}>
    <div class="toolbar" onclick={(e) => e.stopPropagation()}>
        <div class="segmented" role="group" aria-label="Week or day">
            <button type="button" class:on={week.view === 'week'} onclick={() => (week.view = 'week')}>{german ? 'Woche' : 'Week'}</button>
            <button type="button" class:on={week.view === 'day'} onclick={() => (week.view = 'day')}>{german ? 'Tag' : 'Day'}</button>
        </div>

        {#if week.view === 'day'}
            <div class="day-picker" role="group" aria-label="Which day of the schedule">
                <button
                    type="button"
                    onclick={() => week.setShownDay(week.shownDay - 1)}
                    disabled={week.shownDay <= week.weekIndex * 7}
                    aria-label="The previous day"
                >&lsaquo;</button>
                <span class="which">{dayWords(week.shownDay * SECONDS_PER_DAY)}</span>
                <button
                    type="button"
                    onclick={() => week.setShownDay(week.shownDay + 1)}
                    disabled={week.shownDay >= week.weekIndex * 7 + 6}
                    aria-label="The next day"
                >&rsaquo;</button>
            </div>
        {/if}

        {#if week.weeks > 1}
            <!--
                Which week of the cycle. A four-week schedule is twenty-eight day columns and
                nobody can find Thursday in that, so the grid draws one week at a time and this
                says which - the way a calendar shows one month of a year.
            -->
            <div class="weeks" role="group" aria-label="Which week of the schedule">
                <button
                    type="button"
                    onclick={() => (week.weekIndex = Math.max(0, week.weekIndex - 1))}
                    disabled={week.weekIndex === 0}
                    aria-label="The week before"
                >&lsaquo;</button>
                <span class="which">
                    {german ? 'Woche' : 'Week'} {week.weekIndex + 1} {german ? 'von' : 'of'} {week.weeks}
                    {#if week.weekIndex === week.currentWeek}<i class="onair">on air</i>{/if}
                </span>
                <button
                    type="button"
                    onclick={() => (week.weekIndex = Math.min(week.weeks - 1, week.weekIndex + 1))}
                    disabled={week.weekIndex >= week.weeks - 1}
                    aria-label="The week after"
                >&rsaquo;</button>
            </div>
        {/if}

        <label class="zoom">
            {german ? 'Zoom' : 'Zoom'}
            <input
                type="range"
                min={ZOOM_MIN}
                max={ZOOM_MAX}
                step="2"
                value={week.zoom}
                oninput={(e) => {
                    // Taking the slider takes the wheel - but only the wheel. setZoom unticks
                    // "Zoom to now", because two things cannot decide the size; FOLLOWING is
                    // left alone, because following what is on at a zoom you chose yourself is
                    // a perfectly reasonable thing to want.
                    week.setZoom(Number(e.currentTarget.value));
                }}
                aria-label="Zoom"
            />
            <span class="reading">{german ? 'Namen ab ' : 'names from '}{Math.max(1, Math.round(13 / (week.zoom / 60)))} {german ? 'Min.' : 'min'}</span>
        </label>

        <!--
            TWO boxes, because they are two wants.

            These were one, and it did both jobs at once: it sized the grid AND held it on the
            now line, so wanting either without the other was impossible. Separated on the
            owner's instruction - "2 toggles follow and zoom to now".

            Follow decides WHERE the grid is scrolled; it keeps following while you zoom.
            Zoom to now decides HOW BIG things are; a hand on the slider unticks it.
        -->
        <label class="follow" title="Keep the now line in view - including while you zoom">
            <input
                type="checkbox"
                checked={week.frameNow}
                onchange={(e) => week.setFrameNow(e.currentTarget.checked)}
            />
            {german ? 'Aktuelles verfolgen' : "Follow what's on"}
        </label>

        <label class="follow" title="Size the grid so the programme on air now is properly visible, rather than a sliver">
            <input
                type="checkbox"
                checked={week.zoomToNow}
                onchange={(e) => week.setZoomToNow(e.currentTarget.checked)}
            />
            {german ? 'Auf jetzt zoomen' : 'Zoom to now'}
        </label>

        <div class="spacer"></div>

        <!--
            How long the schedule is before it repeats.

            The owner asked for a channel whose whole schedule is two weeks or more, because a
            fortnightly film cannot be said in seven days however the seven days are arranged.
            The number runs the whole range the server will take rather than offering a
            shortlist of "weekly / fortnightly / monthly", which would be an answer disguised
            as a question.

            Shrinking it throws away the weeks past the new end, so it says so, and it is a
            pending edit like everything else here - Undo takes it back.
        -->
        <label class="length" title="Shortening this throws away the weeks past the new end.">
            {german ? 'Wiederholt sich alle' : 'Repeats every'}
            <input
                type="number"
                min="1"
                max={MAX_WEEKS}
                step="1"
                value={week.weeks}
                disabled={week.busy || !week.week?.Curated}
                onchange={(e) => week.setLength(Number(e.currentTarget.value))}
                aria-label="How many weeks the schedule runs for before it repeats"
            />
            {week.weeks === 1 ? 'week' : 'weeks'}
        </label>

        <!--
            The button that is the actual point of a longer schedule. Typing a number works, but
            nobody knows what the number is - a channel of every episode of a series needs
            however many weeks that is, and only the server can say. It lays the week out too,
            because a longer schedule with nothing in the new weeks is a channel that goes dark
            in them.
        -->
        <button
            type="button"
            class="chip"
            onclick={fitToContent}
            disabled={week.busy || !week.week?.Curated}
            title="Works out how long this channel takes to play everything once, makes the schedule that long, and lays it out - so every episode airs before it starts again."
        ><span class="lt-swap">
                <span class="lt-ghost">{german ? 'So lang wie der Inhalt' : 'As long as the content'}</span>
                <span>{week.busy ? (german ? 'Wird bearbeitet…' : 'Working…') : (german ? 'So lang wie der Inhalt' : 'As long as the content')}</span>
            </span></button>

        <div class="legend">
            <span><i style="background: {KIND_FILL.Programme}"></i>{german ? 'Programm' : 'Programme'}</span>
            <span><i style="background: {KIND_FILL.Trailer}"></i>Trailer</span>
            <span><i style="background: {KIND_FILL.Advert}"></i>{german ? 'Werbung' : 'Advert'}</span>
        </div>

        <!--
            The way back. The owner asked for it in the same breath as asking for the week to
            wait for Save: an edit you cannot take back is one you cannot afford to try.

            ALWAYS HERE, disabled when there is nothing to take back. It used to appear only
            once the week was dirty, so the first edit pushed "Lay this week out" sideways and
            the button under the pointer was no longer the one that had been there - "i don't
            like things moving around anywhere!". Reserving the space costs two grey chips and
            is the whole fix.
        -->
        <button
            type="button"
            class="chip"
            onclick={() => week.undo()}
            disabled={week.busy || !week.dirty}
            title={week.dirty ? 'Takes back ' + week.undoWords : 'Nothing to take back yet'}
        >{german ? 'Rückgängig' : 'Undo'}</button>
        <button
            type="button"
            class="chip quiet"
            onclick={() => week.discard()}
            disabled={week.busy || !week.dirty}
            title="Throws away every unsaved schedule change and goes back to what the server holds"
        ><span class="lt-swap">
                <!-- The plural, so the singular cannot be narrower than the box it sits in. -->
                <span class="lt-ghost">Discard {week.pending.length} changes</span>
                <span>Discard {week.pending.length} change{week.pending.length === 1 ? '' : 's'}</span>
            </span></button>

        <button
            type="button"
            class="chip"
            onclick={layOut}
            disabled={week.busy}
            title={store.dirty
                ? 'The week is laid out by the server from the saved configuration, so this saves your changes first.'
                : 'Lays the whole week out again from this channel’s content and settings.'}
        ><span class="lt-swap">
                <!--
                    The widest thing this button can ever say, drawn invisibly underneath it.
                    That is what fixes the width - a number of pixels would be a guess about a
                    font the dashboard chooses, and would be wrong in German.
                -->
                <span class="lt-ghost">{german ? 'Speichern und Woche erstellen' : 'Save and lay this week out'}</span>
                <span>{week.busy ? (german ? 'Wird bearbeitet…' : 'Working…') : store.dirty ? (german ? 'Speichern und Woche erstellen' : 'Save and lay this week out') : (german ? 'Woche erstellen' : 'Lay this week out')}</span>
            </span></button>
    </div>

    {#if week.unsaved}
        <p class="waiting">
            {german ? 'Dieser Kanal wurde noch nicht gespeichert und hat daher keinen Zeitplan auf dem Server. Drücke oben auf ' : 'This channel has not been saved yet, so it has no schedule on the server. Press '}<b>{german ? 'Speichern' : 'Save'}</b>{german ? ' — oder auf ' : ' — or '}<b>{german ? 'Speichern und Woche erstellen' : 'Save and lay this week out'}</b>{german ? ', dann erscheint die Woche hier.' : ' above — and its week appears here.'}
        </p>
    {:else if week.error}
        <p class="bad">{week.error}</p>
    {/if}

    <!--
        The grid is ALWAYS drawn, even while a week is being fetched.

        It used to be replaced by a one-line "Loading the week…" - and the grid's height comes
        from the zoom rather than from its contents, so switching channels collapsed a
        screenful down to a single line and blew it back up again a moment later. That is the
        "weird flickering": not a repaint, a reflow of the whole page twice per channel.

        The week itself is still cleared the instant the channel changes, deliberately - a new
        channel must never be seen wearing the previous one's schedule. So what is drawn here
        for that moment is an empty grid at the right size, and the word sits OVER it instead of
        in place of it.
    -->
    <div class="grid-wrap">
        <Grid {onDropItem} />
        {#if store.isScheduleGenerating(channel.Id)}
            <p class="waiting over">{german ? 'Schedule wird erstellt …' : 'Schedule is being created …'}</p>
        {:else if store.scheduleGenerationError(channel.Id)}
            <p class="bad over">
                {german ? 'Schedule konnte nicht erstellt werden.' : store.scheduleGenerationError(channel.Id)}
                <button type="button" class="retry" onclick={() => store.retrySchedule(channel.Id)}>
                    {german ? 'Erneut versuchen' : 'Retry'}
                </button>
            </p>
        {:else if week.loading}
            <p class="waiting over">{german ? 'Woche wird geladen…' : 'Loading the week…'}</p>
        {/if}
    </div>

    {#if !week.loading}

        {#if selected}
            <div class="inspector" onclick={(e) => e.stopPropagation()}>
                <div class="edge" style="background: {KIND_FILL[selected.Kind]}"></div>
                <div class="what">
                    <div class="name">{selected.Name}</div>
                    <div class="detail">{describe()}</div>
                </div>

                <label class="exact">
                    {german ? 'Start' : 'Starts'}
                    <select
                        value={String(dayOf(selected.StartSecond))}
                        onchange={(e) => setDay(e.currentTarget.value)}
                        aria-label="Which day it airs"
                    >
                        {#each Array.from({ length: week.weeks * 7 }, (_, i) => i) as index (index)}
                            <option value={String(index)}>{dayWords(index * SECONDS_PER_DAY)}</option>
                        {/each}
                    </select>
                    <input
                        type="time"
                        step="60"
                        value={clock(secondOfDay(selected.StartSecond))}
                        onchange={(e) => setStartTime(e.currentTarget.value)}
                        aria-label="What time it starts"
                    />
                </label>
                <button type="button" class="chip" onclick={sameTimeTomorrow} disabled={week.busy}>
                    {german ? 'Morgen zur gleichen Zeit' : 'Same time tomorrow'}
                </button>
                <button
                    type="button"
                    class="chip danger"
                    onclick={() => selected.Id && week.remove(selected.Id)}
                    disabled={week.busy}
                >Take it off</button>
                <button type="button" class="chip quiet" onclick={() => (week.selectedId = null)}>
                    {german ? 'Auswahl aufheben' : 'Clear selection'}
                </button>
            </div>
        {:else}
            <p class="status">{status}</p>
        {/if}
    {/if}

    <Shelf />
</div>

<style>
    .screen {
        flex-grow: 1;
        min-height: 0;
        display: flex;
        flex-direction: column;
    }

    .toolbar {
        display: flex;
        align-items: center;
        gap: 11px;
        padding: 14px 22px 11px;
        flex-wrap: wrap;
    }

    .segmented {
        display: flex;
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        overflow: hidden;
    }

    .segmented button {
        padding: 6px 15px;
        font-size: 13px;
        font-weight: 600;
        font-family: inherit;
        background: none;
        border: none;
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .segmented button.on { background: var(--lt-accent); color: #fff; }

    .zoom {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .zoom input { width: 110px; accent-color: var(--lt-accent); }

    .follow {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        color: var(--lt-text-dim);
        cursor: pointer;
        white-space: nowrap;
    }

    .follow input { accent-color: var(--lt-accent); cursor: pointer; margin: 0; }

    .follow:hover { color: var(--lt-text); }

    .weeks {
        display: flex;
        align-items: center;
        gap: 3px;
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 2px 3px;
    }

    .weeks button {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        font-family: inherit;
        font-size: 15px;
        line-height: 1;
        padding: 3px 8px;
        cursor: pointer;
    }

    .weeks button:hover:not(:disabled) { color: var(--lt-text-strong); }
    .weeks button:disabled { opacity: .3; cursor: default; }

    .day-picker {
        display: flex;
        align-items: center;
        gap: 3px;
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 2px 3px;
    }

    .day-picker button {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        font-family: inherit;
        font-size: 15px;
        line-height: 1;
        padding: 3px 8px;
        cursor: pointer;
    }

    .day-picker button:hover:not(:disabled) { color: var(--lt-text-strong); }
    .day-picker button:disabled { opacity: .3; cursor: default; }

    /* Keep the arrows stationary while the weekday label changes width. */
    .day-picker .which {
        width: 150px;
        text-align: center;
    }

    .which {
        font-size: 12.5px;
        font-weight: 600;
        color: var(--lt-text-title);
        white-space: nowrap;
    }

    .onair {
        font-style: normal;
        font-size: 10.5px;
        font-weight: 700;
        color: var(--lt-accent);
        margin-left: 5px;
    }

    .length {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .length input { width: 4.2em; font-size: 12.5px; padding: 5px 8px; }
    .length input:disabled { opacity: .5; }

    .reading { min-width: 7em; font-size: 11.5px; }

    .spacer { flex-grow: 1; }

    .legend { display: flex; gap: 13px; font-size: 11.5px; color: var(--lt-text-dim); }
    .legend span { display: flex; align-items: center; gap: 5px; }
    .legend i { width: 9px; height: 9px; border-radius: 3px; display: inline-block; }

    .chip {
        display: inline-flex;
        align-items: center;
        gap: 7px;
        padding: 7px 13px;
        border-radius: var(--lt-radius-small);
        background: rgba(255, 255, 255, .05);
        border: 1px solid var(--lt-line-strong);
        font-size: 13px;
        font-weight: 600;
        font-family: inherit;
        color: var(--lt-text-title);
        cursor: pointer;
    }

    .chip:hover:not(:disabled) { background: var(--lt-hover); }
    .chip:disabled { opacity: .55; cursor: default; }

    .chip.danger {
        background: rgba(169, 29, 29, .18);
        border-color: rgba(217, 84, 84, .35);
        color: #e88;
    }

    .chip.quiet { background: none; color: var(--lt-text-dim); font-weight: 500; }

    :global(.screen > .grid) { margin: 0 22px; }

    .inspector {
        margin: 12px 22px 0;
        border: 1px solid rgba(119, 91, 244, .22);
        border-radius: var(--lt-radius);
        padding: 12px 15px;
        display: flex;
        align-items: center;
        gap: 14px;
        background: linear-gradient(90deg, rgba(119, 91, 244, .14) 0%, rgba(119, 91, 244, .02) 100%);
        flex-wrap: wrap;
    }

    .edge { width: 3px; align-self: stretch; border-radius: 2px; min-height: 2.2em; }

    .exact {
        display: inline-flex;
        align-items: center;
        gap: 7px;
        font-size: 12.5px;
        color: var(--lt-text-muted);
        flex: 0 0 auto;
    }

    .exact select, .exact input {
        font-size: 12.5px;
        font-family: inherit;
        padding: 5px 8px;
        color: var(--lt-text);
        background: var(--lt-field-solid);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
    }

    .what { flex-grow: 1; min-width: 0; }

    .name {
        font-size: 14.5px;
        font-weight: 700;
        color: var(--lt-text-strong);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .detail { font-size: 12.5px; color: var(--lt-text-muted); }

    .status, .waiting, .bad {
        margin: 12px 22px 0;
        font-size: 12.5px;
    }

    .status, .waiting { color: var(--lt-text-dim); }

    /*
        The wrapper has to BE the flex child the grid used to be.

        `.grid` is written to take the column's leftover height and scroll inside itself
        (`flex-grow: 1; min-height: 0`, with `.body` scrolling). Wrapping it in a plain relative
        div broke that chain: the wrapper never grew, so the grid took its natural height -
        8283px of it at one zoom - the page stopped scrolling, and the shelf and the inspector
        below were pushed somewhere unreachable. They were still rendered; nobody could get to
        them.

        So the wrapper carries the same three properties, and the grid fills it.
    */
    .grid-wrap {
        position: relative;
        flex-grow: 1;
        min-height: 0;
        display: flex;
        flex-direction: column;
    }

    /*
        Over the grid, not in place of it. Centred on the grid so it reads as the grid being
        busy, rather than as a paragraph appearing above everything and pushing the whole page
        down and back up again on every channel change.
    */
    .waiting.over {
        position: absolute;
        inset: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        margin: 0;
        background: color-mix(in srgb, var(--lt-ground-bottom) 55%, transparent);
        pointer-events: none;
    }
    .bad { color: #e08585; }

    .retry {
        margin-left: 9px;
        padding: 4px 9px;
        border: 1px solid currentColor;
        border-radius: var(--lt-radius-small);
        background: transparent;
        color: inherit;
        font: inherit;
        cursor: pointer;
    }
</style>
