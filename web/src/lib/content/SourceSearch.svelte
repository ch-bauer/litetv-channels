<script lang="ts">
    import { failureWords } from '../jellyfin';
    import { linkHit, search, toSource, type SearchHit } from '../search';
    import { looksLikeAddress } from '../api/playlist';
    import type { ChannelSource } from '../types';
    import { store } from '../config.svelte';

    let { sources }: { sources: ChannelSource[] } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    let term = $state('');
    let hits = $state<SearchHit[]>([]);
    let open = $state(false);
    let busy = $state(false);
    let failed = $state<string | null>(null);
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function run(): Promise<void> {
        const asked = term;
        busy = true;
        failed = null;
        try {
            /*
                An address is looked up as an address and a title as a title, and either way the
                answer is a row in the same list. There used to be a second field beside this
                one for the address - the owner's point was simply that "there are just 2 search
                bars currently" - and a link typed here was silently moved into it, which is a
                box rearranging itself under the hand.
            */
            const found = looksLikeAddress(asked)
                ? [await linkHit(asked)].filter((h): h is SearchHit => h !== null)
                : await search(asked);
            // A slow answer to an old question must not overwrite a newer one.
            if (asked !== term) { return; }
            hits = found;
            open = true;
        } catch (err) {
            failed = failureWords(err);
            open = true;
        } finally {
            busy = false;
        }
    }

    function onInput(): void {
        clearTimeout(timer);
        if (term.trim().length === 0) { hits = []; open = false; return; }
        // A link is read rather than typed-ahead: the wait is a request to YouTube, not a
        // keystroke, so it gets a little longer before it fires.
        timer = setTimeout(run, looksLikeAddress(term) ? 500 : 250);
    }

    /*
        Coming back to the box re-opens what was found last time. The owner asked for this: a
        search that forgets its own answer the moment you look away makes adding three things in
        a row into three searches.
    */
    function onFocus(): void {
        if (hits.length > 0) { open = true; }
    }

    function add(hit: SearchHit): void {
        if (already(hit)) { return; }
        const source = toSource(hit);
        // A new source is opt-in: it must not silently steal probability from the distribution
        // the owner already set. The first and only source is necessarily 100%.
        source.Probability = sources.length === 0 ? 100 : 0;
        sources.push(source);
        // The box is cleared on a link, because an address is a thing you add once and there is
        // nothing else in the answer to pick from. A title search is left alone: adding three
        // episodes of one series in a row should not be three searches.
        if (hit.kind === 'Link') { term = ''; hits = []; open = false; }
    }

    /** Already on the list - by address for a link, by library id for anything else. */
    function already(hit: SearchHit): boolean {
        return hit.kind === 'Link'
            ? sources.some((s) => s.Url === hit.url)
            : sources.some((s) => s.ItemId === hit.id);
    }

    /** What the tag on a row reads. */
    function tagFor(hit: SearchHit): string {
        if (hit.kind === 'Series') { return 'SERIES'; }
        if (hit.kind === 'Collection') { return 'COLLECTION'; }
        if (hit.kind === 'Episode') { return 'EPISODE'; }
        if (hit.kind === 'Link') { return hit.videoCount === undefined ? 'LINK' : 'PLAYLIST'; }
        return 'FILM';
    }
</script>

<div class="search">
    <input
        type="search"
        bind:value={term}
        oninput={onInput}
        onfocus={onFocus}
        onkeydown={(e) => { if (e.key === 'Escape') { open = false; } }}
        placeholder={german ? 'Filme, Serien, Episoden und Sammlungen suchen — oder Link einfügen…' : 'Search films, series, episodes and collections — or paste a link…'}
        aria-label={german ? 'Bibliothek durchsuchen oder Link einfügen' : 'Search the library, or paste a link'}
    />
    {#if busy}<span class="busy">{german ? 'suche…' : 'searching…'}</span>{/if}
    {#if open && hits.length > 0}
        <button class="hide" type="button" onclick={() => (open = false)}>{german ? 'ausblenden' : 'hide'}</button>
    {/if}
</div>

{#if open}
    <div class="results">
        {#if failed}
            <p class="bad">{german ? 'Suche fehlgeschlagen: ' : 'That search failed: '}{failed}</p>
        {:else if hits.length === 0 && !busy}
            <p class="none">{german ? 'Keine passenden Titel in der Bibliothek für „' : 'Nothing in the library matches “'}{term}{german ? '“.' : '”.'}</p>
        {:else}
            {#each hits as hit (hit.kind + '|' + hit.id + '|' + (hit.url ?? ''))}
                <button class="hit" type="button" onclick={() => add(hit)} disabled={already(hit)}>
                    <span class="name">{hit.name}</span>
                    <span class="detail">{hit.detail}</span>
                    <span
                        class="kind"
                        class:series={hit.kind === 'Series'}
                        class:collection={hit.kind === 'Collection'}
                        class:episode={hit.kind === 'Episode'}
                        class:link={hit.kind === 'Link'}
                    >
                        {tagFor(hit)}
                    </span>
                    <span class="verb">{already(hit) ? (german ? 'hinzugefügt' : 'added') : '+'}</span>
                </button>
            {/each}
        {/if}
    </div>
{/if}

<style>
    .search {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 12px 13px;
        background: var(--lt-card-inset);
    }

    input {
        flex: 1 1 0;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    input::placeholder { color: var(--lt-text-dim); }

    .busy, .hide {
        font-size: 11.5px;
        color: var(--lt-text-dim);
        background: none;
        border: none;
        cursor: pointer;
        font-family: inherit;
    }

    .results {
        max-height: 15em;
        overflow-y: auto;
        border-top: 1px solid var(--lt-line-soft);
    }

    .hit {
        display: flex;
        align-items: center;
        gap: 10px;
        width: 100%;
        padding: 9px 13px;
        background: none;
        border: none;
        border-bottom: 1px solid var(--lt-line-soft);
        color: inherit;
        font-family: inherit;
        text-align: left;
        cursor: pointer;
    }

    .hit:hover:not(:disabled) { background: var(--lt-hover); }
    .hit:disabled { opacity: 0.45; cursor: default; }

    .name {
        flex-grow: 1;
        min-width: 0;
        font-size: 13px;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .detail { flex: 0 0 auto; font-size: 11.5px; color: var(--lt-text-dim); }

    .kind {
        flex: 0 0 auto;
        padding: 2px 7px;
        border-radius: 999px;
        background: rgba(255, 255, 255, 0.07);
        color: var(--lt-text-muted);
        font-size: 10px;
        font-weight: 700;
    }

    .kind.series { background: var(--lt-series-bg); color: var(--lt-series); }
    .kind.collection { background: var(--lt-collection-bg); color: var(--lt-collection); }
    .kind.episode { background: var(--lt-episode-bg); color: var(--lt-episode); }
    .kind.link { background: var(--lt-link-bg); color: var(--lt-link); }

    .verb { flex: 0 0 auto; font-size: 12px; color: var(--lt-text-dim); }

    .none, .bad { padding: 12px 13px; margin: 0; font-size: 12.5px; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
