<script lang="ts">
    /*
        The Week and Day views, and the shelf under them. One screen: Day is the same grid at a
        closer zoom showing one column, which is why they share everything.
    */
    import Grid from '../lib/week/Grid.svelte';
    import Shelf from '../lib/week/Shelf.svelte';
    import { week } from '../lib/week/weekStore.svelte';
    import { store } from '../lib/config.svelte';
    import { resolveDuration } from '../lib/api/duration';
    import { DAY_NAMES, KIND_FILL, clock, dayOf, secondOfDay } from '../lib/api/week';
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();

    $effect(() => {
        // Reloads whenever the channel changes, and only then.
        void week.load(channel.Id);
    });

    week.restoreZoom();

    const selected = $derived(week.selected);

    const status = $derived.by(() => {
        if (!week.week) { return ''; }
        if (!week.week.Curated) {
            return 'No week laid out — this channel airs from its content and settings.';
        }
        const placed = week.airings.filter((a) => a.Kind !== 'Gap').length;
        const day = week.view === 'day' ? DAY_NAMES[dayOf(new Date().getDay())] : null;
        return placed + ' scheduled' + (day ? ' · ' + day : '');
    });

    function describe(): string {
        if (!selected) { return ''; }
        const day = DAY_NAMES[dayOf(selected.StartSecond)];
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
    async function layOut(): Promise<void> {
        if (store.dirty) {
            await store.save();
            // save() reports its own failure. Still dirty means it did not land, and laying the
            // week out now would quietly use the old content.
            if (store.dirty) { return; }
        }
        await week.generate();
    }

    async function onDropItem(payload: string, second: number): Promise<void> {
        let dropped: { itemId: string | null; url: string | null; name: string };
        try {
            dropped = JSON.parse(payload) as typeof dropped;
        } catch {
            // Something else was dragged onto the grid. Nothing to do.
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

<div class="screen">
    <div class="toolbar">
        <div class="segmented" role="group" aria-label="Week or day">
            <button type="button" class:on={week.view === 'week'} onclick={() => (week.view = 'week')}>Week</button>
            <button type="button" class:on={week.view === 'day'} onclick={() => (week.view = 'day')}>Day</button>
        </div>

        <label class="zoom">
            Zoom
            <input
                type="range"
                min="8"
                max="1200"
                step="2"
                value={week.zoom}
                oninput={(e) => week.setZoom(Number(e.currentTarget.value))}
                aria-label="Zoom"
            />
            <span class="reading">names from {Math.max(1, Math.round(13 / (week.zoom / 60)))} min</span>
        </label>

        <div class="spacer"></div>

        <div class="legend">
            <span><i style="background: {KIND_FILL.Programme}"></i>Programme</span>
            <span><i style="background: {KIND_FILL.Trailer}"></i>Trailer</span>
            <span><i style="background: {KIND_FILL.Advert}"></i>Advert</span>
        </div>

        <button
            type="button"
            class="chip"
            onclick={layOut}
            disabled={week.busy}
            title={store.dirty
                ? 'The week is laid out by the server from the saved configuration, so this saves your changes first.'
                : 'Lays the whole week out again from this channel’s content and settings.'}
        >
            {week.busy ? 'Working…' : store.dirty ? 'Save and lay this week out' : 'Lay this week out'}
        </button>
    </div>

    {#if week.error}
        <p class="bad">{week.error}</p>
    {/if}

    {#if week.loading}
        <p class="waiting">Loading the week…</p>
    {:else}
        <Grid {onDropItem} />

        {#if selected}
            <div class="inspector">
                <div class="edge" style="background: {KIND_FILL[selected.Kind]}"></div>
                <div class="what">
                    <div class="name">{selected.Name}</div>
                    <div class="detail">{describe()}</div>
                </div>
                <button type="button" class="chip" onclick={sameTimeTomorrow} disabled={week.busy}>
                    Same time tomorrow
                </button>
                <button
                    type="button"
                    class="chip danger"
                    onclick={() => selected.Id && week.remove(selected.Id)}
                    disabled={week.busy}
                >Take it off</button>
                <button type="button" class="chip quiet" onclick={() => (week.selectedId = null)}>
                    Clear selection
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
    .bad { color: #e08585; }
</style>
