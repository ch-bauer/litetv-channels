<script lang="ts">
    import { store } from '../config.svelte';
    import { absolute } from '../jellyfin';
    import type { ChannelSource } from '../types';

    // The array itself, not the channel: a block's sources are edited with the same list.
    let { sources, empty = 'Nothing yet - find something below.' }:
        { sources: ChannelSource[]; empty?: string } = $props();

    // Which row the keyboard is on. The owner asked for shortcuts - select a row, press Delete,
    // it goes; move one with the arrow keys - so a row has to have a notion of being selected
    // in the first place, which the old page never had.
    let selected = $state<number | null>(null);
    let dragging = $state<number | null>(null);
    let over = $state<number | null>(null);

    function art(source: ChannelSource): string | null {
        // A YouTube source has no library item, so there is no library picture to ask for -
        // asking anyway would put a broken image in every row.
        if (source.Type === 'YouTube') { return null; }
        return absolute('/Items/' + source.ItemId + '/Images/Primary?maxHeight=92&quality=90');
    }

    function kindLabel(source: ChannelSource): string {
        if (source.Type === 'Series') { return 'SERIES'; }
        if (source.Type === 'Collection') { return 'COLLECTION'; }
        if (source.Type === 'YouTube') { return 'YOUTUBE'; }
        return 'FILM';
    }

    function detailOf(source: ChannelSource): string {
        if (source.Type === 'Series') { return 'series'; }
        if (source.Type === 'Collection') { return 'collection'; }
        if (source.Type === 'YouTube') { return source.Url ?? 'playlist'; }
        return 'film';
    }

    function move(from: number, to: number): void {
        if (to < 0 || to >= sources.length || from === to) { return; }
        const list = sources;
        const [taken] = list.splice(from, 1);
        list.splice(to, 0, taken);
        selected = to;
        store.touch();
    }

    function remove(index: number): void {
        sources.splice(index, 1);
        if (selected !== null && selected >= sources.length) {
            selected = sources.length ? sources.length - 1 : null;
        }
        store.touch();
    }

    function onKey(event: KeyboardEvent, index: number): void {
        if (event.key === 'Delete' || event.key === 'Backspace') {
            event.preventDefault();
            remove(index);
            return;
        }
        // Alt with an arrow moves the row; the arrow alone walks between rows. Without the
        // modifier there would be no way to pass over a row without dragging it along.
        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            event.preventDefault();
            const step = event.key === 'ArrowUp' ? -1 : 1;
            if (event.altKey) { move(index, index + step); } else { focusRow(index + step); }
        }
    }

    function focusRow(index: number): void {
        if (index < 0 || index >= sources.length) { return; }
        selected = index;
        const row = document.querySelector<HTMLElement>('[data-source-row="' + index + '"]');
        row?.focus();
    }

    function onDrop(target: number): void {
        if (dragging !== null) { move(dragging, target); }
        dragging = null;
        over = null;
    }
</script>

<div class="list" role="listbox" aria-label="What this channel plays" tabindex="-1">
    {#each sources as source, index (source.ItemId + ':' + index)}
        <div
            role="option"
            aria-selected={selected === index}
            class="row"
            class:selected={selected === index}
            class:over={over === index && dragging !== index}
            data-source-row={index}
            tabindex="0"
            draggable="true"
            onclick={() => (selected = index)}
            onfocus={() => (selected = index)}
            onkeydown={(e) => onKey(e, index)}
            ondragstart={() => (dragging = index)}
            ondragend={() => { dragging = null; over = null; }}
            ondragover={(e) => { e.preventDefault(); over = index; }}
            ondrop={(e) => { e.preventDefault(); onDrop(index); }}
        >
            <svg class="grip" width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <circle cx="9" cy="5" r="1.7" /><circle cx="9" cy="12" r="1.7" /><circle cx="9" cy="19" r="1.7" />
                <circle cx="15" cy="5" r="1.7" /><circle cx="15" cy="12" r="1.7" /><circle cx="15" cy="19" r="1.7" />
            </svg>

            {#if art(source)}
                <img class="art" src={art(source)} alt="" loading="lazy" onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')} />
            {:else}
                <span class="art placeholder" aria-hidden="true">▶</span>
            {/if}

            <div class="who">
                <div class="name" title={source.Name}>{source.Name}</div>
                <div class="detail" title={detailOf(source)}>{detailOf(source)}</div>
            </div>

            <span
                class="kind"
                class:series={source.Type === 'Series'}
                class:collection={source.Type === 'Collection'}
                class:youtube={source.Type === 'YouTube'}
            >{kindLabel(source)}</span>

            <button class="bin" type="button" title="Remove from this channel" onclick={(e) => { e.stopPropagation(); remove(index); }}>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
                    <path d="M5 7h14M10 11v6M14 11v6M6 7l1 12.5a1.5 1.5 0 0 0 1.5 1.5h7a1.5 1.5 0 0 0 1.5-1.5L18 7M9.5 7V4.5A1.5 1.5 0 0 1 11 3h2a1.5 1.5 0 0 1 1.5 1.5V7" />
                </svg>
            </button>
        </div>
    {:else}
        <p class="none">{empty}</p>
    {/each}
</div>

<style>
    .row {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 10px 13px;
        border-bottom: 1px solid var(--lt-line-soft);
        cursor: default;
    }

    .row:hover { background: var(--lt-hover); }

    /* Selected is a rule down the edge, not a colour wash: it has to survive hover. */
    .selected { box-shadow: inset 2px 0 0 var(--lt-accent); }
    .over { background: rgba(119, 91, 244, 0.12); }

    .grip { flex: 0 0 auto; color: rgba(255, 255, 255, 0.28); cursor: grab; }
    .grip:active { cursor: grabbing; }

    .art {
        flex: 0 0 34px;
        width: 34px;
        height: 46px;
        border-radius: 4px;
        object-fit: cover;
        background: linear-gradient(160deg, #2a3a4a, #1d2635);
    }

    .who { flex-grow: 1; min-width: 0; }

    .name {
        font-size: 13.5px;
        font-weight: 600;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .detail { font-size: 11.5px; color: var(--lt-text-dim); margin-top: 2px; }

    .kind {
        flex: 0 0 auto;
        padding: 2px 8px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.07);
        color: var(--lt-text-muted);
        font-size: 10.5px;
        font-weight: 700;
    }

    .kind.series { background: var(--lt-series-bg); color: var(--lt-series); }
    .kind.collection { background: var(--lt-collection-bg); color: var(--lt-collection); }
    .kind.youtube { background: rgba(224, 90, 90, .18); color: #e88; }

    .art.placeholder {
        display: flex;
        align-items: center;
        justify-content: center;
        color: rgba(255, 255, 255, .45);
        font-size: 13px;
    }

    .detail {
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .bin {
        flex: 0 0 auto;
        background: none;
        border: none;
        padding: 2px;
        color: rgba(255, 255, 255, 0.35);
        cursor: pointer;
    }

    .bin:hover { color: #e08585; }

    .none { padding: 14px 13px; margin: 0; font-size: 12.5px; color: var(--lt-text-dim); }
</style>
