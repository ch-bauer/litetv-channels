<script lang="ts">
    import { absolute } from '../jellyfin';
    import { fetchPlaylist } from '../api/playlist';
    import type { ChannelSource } from '../types';
    import { store } from '../config.svelte';

    // The array itself, not the channel: a block's sources are edited with the same list.
    let { sources, empty = 'Nothing yet - find something below.' }:
        { sources: ChannelSource[]; empty?: string } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    /*
        Which row the keyboard is on. The owner asked for shortcuts - select a row, press Delete,
        it goes; move one with the arrow keys - so a row has to have a notion of being selected
        in the first place, which the old page never had.

        It used to be an index that only a click on the row itself, or on the thin strip of list
        below the last row, could clear. Clicking anywhere else on the page left a row marked
        selected with the keyboard nowhere near it - a selection that could not be let go of
        without hunting the row down again, which is what the owner reported as not being able
        to unselect. Turning your attention elsewhere is what lets it go now; see below.
    */
    let selected = $state<number | null>(null);
    let scope = $state<HTMLElement | null>(null);

    /*
        Letting go, said in terms of what somebody DID.

        The first attempt at this watched `focusout` from the list, which was almost right and
        failed on the one case that matters: pressing "Move up" re-draws the list, the focused
        button goes with it, and a destroyed element reports itself exactly like a focus that
        left - so the bar threw the selection away in the middle of using it.

        A press outside the list, or the keyboard being put somewhere outside it, are things a
        person does. Neither can be manufactured by a re-render, and between them they cover
        every way of turning your attention elsewhere: clicking any other part of the page, and
        tabbing out of the list. Escape and clicking the row again are still here as well.

        Both `pointerdown` and `mousedown`, because they are not the same reach: a pen or a touch
        raises the first and not always the second, and anything driving the page - the test
        suite included - raises the second and not the first. Two listeners for one fact is
        cheap; clearing an already-clear selection costs nothing.
    */
    $effect(() => {
        const away = (event: Event) => {
            const target = event.target as Node | null;
            if (scope && target && !scope.contains(target)) { selected = null; }
        };
        const on = ['pointerdown', 'mousedown', 'focusin'];
        for (const name of on) { document.addEventListener(name, away, true); }
        return () => {
            for (const name of on) { document.removeEventListener(name, away, true); }
        };
    });

    let dragging = $state<number | null>(null);
    let over = $state<number | null>(null);
    const youtubeArt = $state<Record<string, string>>({});
    const youtubeAsked = new Set<string>();

    function youtubeVideoId(value: string): string | null {
        try {
            const url = new URL(value);
            if (url.hostname === 'youtu.be') { return url.pathname.slice(1).split('/')[0] || null; }
            const watched = url.searchParams.get('v');
            if (watched) { return watched; }
            const match = url.pathname.match(/\/(?:shorts|embed|live)\/([^/?]+)/i);
            return match?.[1] ?? null;
        } catch { return null; }
    }

    function youtubeImage(source: ChannelSource): string | null {
        if (source.Type !== 'YouTube' || !source.Url) { return null; }
        const id = youtubeVideoId(source.Url);
        return id ? `https://i.ytimg.com/vi/${encodeURIComponent(id)}/hqdefault.jpg` : youtubeArt[source.Url] ?? null;
    }

    // A playlist has no single video id. Use its first resolved item as the source preview;
    // direct video links can use YouTube's thumbnail URL immediately.
    $effect(() => {
        for (const source of sources) {
            if (source.Type !== 'YouTube' || !source.Url || youtubeAsked.has(source.Url)) { continue; }
            youtubeAsked.add(source.Url);
            const direct = youtubeImage(source);
            if (direct) { youtubeArt[source.Url] = direct; continue; }
            void fetchPlaylist(source.Url).then((playlist) => {
                const id = youtubeVideoId(playlist.Items[0]?.Url ?? '');
                if (id) { youtubeArt[source.Url!] = `https://i.ytimg.com/vi/${encodeURIComponent(id)}/hqdefault.jpg`; }
            }).catch(() => { /* the placeholder remains if YouTube is unavailable */ });
        }
    });

    function art(source: ChannelSource): string | null {
        // A YouTube source has no library item, so there is no library picture to ask for -
        // asking anyway would put a broken image in every row.
        if (source.Type === 'YouTube') { return null; }
        return absolute('/Items/' + source.ItemId + '/Images/Primary?maxHeight=92&quality=90');
    }

    function kindLabel(source: ChannelSource): string {
        if (source.Type === 'Series') { return german ? 'SERIE' : 'SERIES'; }
        if (source.Type === 'Collection') { return german ? 'SAMMLUNG' : 'COLLECTION'; }
        if (source.Type === 'YouTube') { return 'YOUTUBE'; }
        return german ? 'FILM' : 'FILM';
    }

    function detailOf(source: ChannelSource): string {
        if (source.Type === 'Series') { return german ? 'Serie' : 'series'; }
        if (source.Type === 'Collection') { return german ? 'Sammlung' : 'collection'; }
        if (source.Type === 'YouTube') { return source.Url ?? 'playlist'; }
        return german ? 'Film' : 'film';
    }

    function move(from: number, to: number): void {
        if (to < 0 || to >= sources.length || from === to) { return; }
        const list = sources;
        const [taken] = list.splice(from, 1);
        list.splice(to, 0, taken);
        selected = to;
    }

    function remove(index: number): void {
        sources.splice(index, 1);
        distributeEvenly();
        if (selected !== null && selected >= sources.length) {
            selected = sources.length ? sources.length - 1 : null;
        }
    }

    const probabilityTotal = $derived(sources.reduce((total, source) => total + (source.Probability ?? 100), 0));

    /** Makes the listed values a genuine 100% distribution, preserving their proportions. */
    function distributeEvenly(): void {
        if (sources.length === 0) { return; }
        const total = sources.reduce((sum, source) => sum + Math.max(0, source.Probability ?? 100), 0);
        let assigned = 0;
        for (let i = 0; i < sources.length; i++) {
            const source = sources[i]!;
            const value = total > 0
                ? Math.round((Math.max(0, source.Probability ?? 100) / total) * 100)
                : Math.floor(100 / sources.length);
            source.Probability = i === sources.length - 1 ? 100 - assigned : value;
            assigned += source.Probability;
        }
    }

    /** Whether every source already carries the same share - so the button has nothing to fix. */
    const allEqual = $derived.by(() => {
        if (sources.length === 0) { return true; }
        const first = sources[0]!.Probability ?? 100;
        return sources.every((source) => (source.Probability ?? 100) === first);
    });

    /*
        Distinct from "distribute to 100%": that one keeps whatever ratio is already there and
        only rescales it to add up to 100, so a channel weighted 50/30/20 stays 50/30/20. This
        throws the ratio away and gives every source the same share instead - the report was a
        weighted-random channel where the sources plainly were not equally likely, and rescaling
        an unequal split does not touch that at all.
    */
    function equalizeAll(): void {
        if (sources.length === 0) { return; }
        const base = Math.floor(100 / sources.length);
        const remainder = 100 - (base * sources.length);
        for (let i = 0; i < sources.length; i++) {
            sources[i]!.Probability = base + (i < remainder ? 1 : 0);
        }
    }

    function setProbability(index: number, value: number): void {
        const next = Math.max(0, Math.min(100, Math.floor(value)));
        const others = sources.filter((_, candidate) => candidate !== index);
        sources[index].Probability = next;
        if (others.length === 0) { return; }

        const remainder = 100 - next;
        const total = others.reduce((sum, source) => sum + Math.max(0, source.Probability ?? 0), 0);
        let assigned = 0;
        for (let i = 0; i < others.length; i++) {
            const other = others[i]!;
            const value = total > 0
                ? Math.round((Math.max(0, other.Probability ?? 0) / total) * remainder)
                : Math.floor(remainder / others.length);
            other.Probability = i === others.length - 1 ? remainder - assigned : value;
            assigned += other.Probability;
        }
    }

    /*
        What was selected before the press. Clicking a row focuses it first, and focus selects
        it - so a plain "is it already selected" test in the click would see the selection the
        focus had just made and clear it immediately. Reading it at mousedown is the honest
        answer to "was this row already the selected one when you clicked it".
    */
    let selectedAtPress: number | null = null;

    /** Clicking the selected row again clears it. Escape clears it too. */
    function press(index: number): void {
        selected = selectedAtPress === index ? null : index;
    }

    function onKey(event: KeyboardEvent, index: number): void {
        if (event.key === 'Escape') {
            selected = null;
            (document.activeElement as HTMLElement | null)?.blur();
            return;
        }

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

    const chosen = $derived(selected === null ? null : sources[selected] ?? null);

    function onDrop(target: number): void {
        if (dragging !== null) { move(dragging, target); }
        dragging = null;
        over = null;
    }
</script>

<!--
    What selecting a row is FOR was invisible: it armed the keyboard and nothing said so, which
    is why the owner asked what selecting an item even does. The bar under the list answers it,
    and the same actions are there as buttons for anyone who would rather not learn a shortcut.
-->
<!--
    A click on the list's own ground - below the last row - lets the selection go. Clicking the
    selected row again does too, and has since this list was built, but a row is a small target
    and a click that begins on it and drifts a pixel becomes a DRAG rather than a click, which
    is a selection that will not clear however many times it is pressed.
-->
<!--
    The list and the bar under it are ONE place to be: the bar's buttons are the selected row's
    own actions, so focus moving from a row to "Move up" has not left the selection behind. Focus
    going anywhere else has, and lets it go.
-->
<div class="scope" bind:this={scope}>
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<!-- svelte-ignore a11y_click_events_have_key_events -->
<div
    class="list"
    role="listbox"
    aria-label="What this channel plays"
    tabindex="-1"
    onclick={() => (selected = null)}
>
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
            onmousedown={() => (selectedAtPress = selected)}
            onclick={(e) => { e.stopPropagation(); press(index); }}
            onfocus={() => (selected = index)}
            onkeydown={(e) => onKey(e, index)}
            ondragstart={() => { dragging = index; selectedAtPress = null; }}
            ondragend={() => { dragging = null; over = null; }}
            ondragover={(e) => { e.preventDefault(); over = index; }}
            ondrop={(e) => { e.preventDefault(); onDrop(index); }}
        >
            <svg class="grip" width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <circle cx="9" cy="5" r="1.7" /><circle cx="9" cy="12" r="1.7" /><circle cx="9" cy="19" r="1.7" />
                <circle cx="15" cy="5" r="1.7" /><circle cx="15" cy="12" r="1.7" /><circle cx="15" cy="19" r="1.7" />
            </svg>

            {#if youtubeImage(source) ?? art(source)}
                <img class="art" src={youtubeImage(source) ?? art(source) ?? undefined} alt="" loading="lazy" onerror={(e) => ((e.currentTarget as HTMLImageElement).style.visibility = 'hidden')} />
            {:else}
                <span class="art placeholder" aria-hidden="true">▶</span>
            {/if}

            <div class="who">
                <div class="name" title={source.Name}>{source.Name}</div>
                <div class="detail" title={detailOf(source)}>{detailOf(source)}</div>
            </div>

            <label class="probability">
                <input
                    type="number"
                    min="0"
                    max="100"
                    value={source.Probability ?? 100}
                    oninput={(e) => {
                        const n = Number(e.currentTarget.value);
                        setProbability(index, Number.isFinite(n) ? n : 0);
                    }}
                    aria-label={german ? `Wahrscheinlichkeit für ${source.Name}` : `Probability for ${source.Name}`}
                    onclick={(e) => e.stopPropagation()}
                />
                <span>%</span>
            </label>

            <span
                class="kind"
                class:series={source.Type === 'Series'}
                class:collection={source.Type === 'Collection'}
                class:youtube={source.Type === 'YouTube'}
            >{kindLabel(source)}</span>

            <button class="bin" type="button" title={german ? 'Aus diesem Kanal entfernen' : 'Remove from this channel'} onclick={(e) => { e.stopPropagation(); remove(index); }}>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
                    <path d="M5 7h14M10 11v6M14 11v6M6 7l1 12.5a1.5 1.5 0 0 0 1.5 1.5h7a1.5 1.5 0 0 0 1.5-1.5L18 7M9.5 7V4.5A1.5 1.5 0 0 1 11 3h2a1.5 1.5 0 0 1 1.5 1.5V7" />
                </svg>
            </button>
        </div>
    {:else}
        <p class="none">{empty}</p>
    {/each}
</div>

{#if sources.length > 0}
    <div class:wrong-total={probabilityTotal !== 100} class="probability-total">
        <span>{german ? 'Gewichtung insgesamt:' : 'Total weight:'} {probabilityTotal}%</span>
        {#if probabilityTotal !== 100}
            <button type="button" onclick={distributeEvenly}>{german ? 'auf 100 % verteilen' : 'distribute to 100%'}</button>
        {/if}
        {#if sources.length > 1 && !allEqual}
            <button
                type="button"
                onclick={equalizeAll}
                title={german
                    ? 'Setzt jede Quelle auf den gleichen Anteil, statt das bestehende Verhältnis nur auf 100 % zu skalieren.'
                    : 'Gives every source the same share, instead of only rescaling the existing ratio to 100%.'}
            >{german ? 'alle gleich gewichten' : 'make all equal'}</button>
        {/if}
    </div>
{/if}

{#if chosen}
    <div class="chosen">
        <span class="chosen-name" title={chosen.Name}>{chosen.Name}</span>
        <span class="chosen-what">{german ? 'ist ausgewählt —' : 'is selected —'}</span>
        <button type="button" onclick={() => selected !== null && move(selected, selected - 1)} disabled={selected === 0}>
            {german ? 'Nach oben' : 'Move up'}
        </button>
        <button
            type="button"
            onclick={() => selected !== null && move(selected, selected + 1)}
            disabled={selected === sources.length - 1}
        >{german ? 'Nach unten' : 'Move down'}</button>
        <button type="button" class="danger" onclick={() => selected !== null && remove(selected)}>{german ? 'Entfernen' : 'Remove'}</button>
        <span class="chosen-keys">{german ? 'oder Alt↑/Alt↓ zum Verschieben, Entf zum Entfernen, Esc zum Aufheben' : 'or Alt↑/Alt↓ to move, Delete to remove, Escape to let go'}</span>
    </div>
{/if}
</div>

<style>
    /* Grouping only - the card's own flow is what draws this, and a wrapper must not join in. */
    .scope { display: contents; }

    .chosen {
        display: flex;
        align-items: center;
        gap: 9px;
        flex-wrap: wrap;
        padding: 8px 13px;
        border-top: 1px solid var(--lt-line);
        background: var(--lt-accent-soft);
        font-size: 12px;
        color: var(--lt-text-muted);
    }

    .probability-total {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 7px 13px;
        border-top: 1px solid var(--lt-line-soft);
        font-size: 11.5px;
        color: var(--lt-text-muted);
    }

    .probability-total.wrong-total { color: #e0a16e; }
    .probability-total button {
        border: 0;
        padding: 0;
        background: none;
        color: var(--lt-accent);
        font: inherit;
        cursor: pointer;
    }

    .chosen-name {
        font-weight: 600;
        color: var(--lt-text-title);
        max-width: 240px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .chosen button {
        background: none;
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        color: var(--lt-text);
        font-family: inherit;
        font-size: 11.5px;
        padding: 3px 8px;
        cursor: pointer;
    }

    .chosen button:disabled { opacity: .45; cursor: default; }
    .chosen button.danger { color: #e08585; border-color: rgba(224, 133, 133, .3); }

    .chosen-keys { color: var(--lt-text-faint); font-size: 11px; }

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

    .probability { display: flex; align-items: center; gap: 3px; flex: 0 0 auto; color: var(--lt-text-muted); font-size: 11px; }
    .probability input { width: 47px; padding: 4px 3px; border: 1px solid var(--lt-line-strong); border-radius: var(--lt-radius-small); background: var(--lt-bg); color: var(--lt-text); font: inherit; text-align: right; }

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

    /* Room to click below the last row, so "let go" has somewhere to be done. */
    .list { padding-bottom: 10px; }
</style>
