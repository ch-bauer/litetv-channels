<script lang="ts">
    import { search, toSource, type SearchHit } from '../search';
    import { fetchPlaylist, looksLikeAddress, looksLikePlaylist } from '../api/playlist';
    import type { ChannelSource } from '../types';

    let { sources }: { sources: ChannelSource[] } = $props();

    let playlist = $state('');
    let fetching = $state(false);
    let playlistNote = $state<{ text: string; bad: boolean } | null>(null);

    /*
        A YouTube playlist as CONTENT, not as something inside a break. It has no library item
        behind it, so it is named by its address; the server expands it to its videos when the
        queue is built, and never stores that list - so a playlist that gains a video reaches
        the channel the next time the week is laid out.
    */
    /*
        The playlist is READ before it is added. It used to be taken on trust: a row called
        "YouTube playlist" appeared whatever the address was, and whether it held four hundred
        videos or nothing at all was invisible until a week was laid out. The owner reported that
        as "fetching a YouTube playlist does not work", and they were right - nothing fetched.

        What is stored is still only the address. The server expands it afresh every time a week
        is laid out, so a playlist that gains a video is picked up then.
    */
    async function addPlaylist(): Promise<void> {
        const url = playlist.trim();
        if (!looksLikePlaylist(url)) { return; }
        if (sources.some((s) => s.Url === url)) {
            playlistNote = { text: 'That playlist is already on the list.', bad: true };
            return;
        }

        fetching = true;
        playlistNote = null;
        try {
            const found = await fetchPlaylist(url);
            if (found.Items.length === 0) {
                playlistNote = {
                    text: 'YouTube gave nothing back for that address. A private or deleted '
                        + 'playlist looks exactly like this.',
                    bad: true,
                };
                return;
            }

            sources.push({
                Type: 'YouTube',
                ItemId: '00000000-0000-0000-0000-000000000000',
                // Named by what is in it, so the row says something. The first title is the one
                // thing about a playlist a person recognises.
                Name: found.Items.length + ' videos - ' + found.Items[0].Title,
                Url: url,
            });
            playlistNote = {
                text: 'Added ' + found.Items.length + ' videos. They are read again every time the '
                    + 'week is laid out, so the list stays current.',
                bad: false,
            };
            playlist = '';
        } catch (err) {
            playlistNote = {
                text: 'That playlist could not be read: '
                    + (err instanceof Error ? err.message : String(err)),
                bad: true,
            };
        } finally {
            fetching = false;
        }
    }

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
            const found = await search(asked);
            // A slow answer to an old question must not overwrite a newer one.
            if (asked !== term) { return; }
            hits = found;
            open = true;
        } catch (err) {
            failed = err instanceof Error ? err.message : String(err);
            open = true;
        } finally {
            busy = false;
        }
    }

    /*
        A link pasted into the search box is a link. It is moved into the playlist field rather
        than searched for, because searching the library for an address can only ever answer
        "nothing matches" - which reads as the search being broken, not as the box being the
        wrong one.
    */
    const termIsAddress = $derived(looksLikeAddress(term));

    function onInput(): void {
        clearTimeout(timer);
        if (termIsAddress) {
            hits = [];
            open = false;
            playlist = term.trim();
            term = '';
            return;
        }
        if (term.trim().length === 0) { hits = []; open = false; return; }
        timer = setTimeout(run, 250);
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
        if (sources.some((s) => s.ItemId === hit.id)) { return; }
        sources.push(toSource(hit));
    }

    function already(hit: SearchHit): boolean {
        return sources.some((s) => s.ItemId === hit.id);
    }
</script>

<div class="search">
    <input
        type="search"
        bind:value={term}
        oninput={onInput}
        onfocus={onFocus}
        onkeydown={(e) => { if (e.key === 'Escape') { open = false; } }}
        placeholder="Search films, series and collections…"
        aria-label="Search films, series and collections"
    />
    {#if busy}<span class="busy">searching…</span>{/if}
    {#if open && hits.length > 0}
        <button class="hide" type="button" onclick={() => (open = false)}>hide</button>
    {/if}
</div>

<div class="search playlist-row">
    <input
        type="url"
        bind:value={playlist}
        onkeydown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addPlaylist(); } }}
        placeholder="…or a YouTube playlist address"
        aria-label="Add a YouTube playlist as content"
    />
    <button
        class="add-playlist"
        type="button"
        onclick={addPlaylist}
        disabled={fetching || !looksLikePlaylist(playlist)}
    >
        {fetching ? 'Reading it...' : 'Add playlist'}
    </button>
</div>

{#if playlistNote}
    <p class="playlist-note" class:bad={playlistNote.bad}>{playlistNote.text}</p>
{/if}

{#if open}
    <div class="results">
        {#if failed}
            <p class="bad">That search failed: {failed}</p>
        {:else if hits.length === 0 && !busy}
            <p class="none">Nothing in the library matches “{term}”.</p>
        {:else}
            {#each hits as hit (hit.id)}
                <button class="hit" type="button" onclick={() => add(hit)} disabled={already(hit)}>
                    <span class="name">{hit.name}</span>
                    <span class="detail">{hit.detail}</span>
                    <span class="kind" class:series={hit.kind === 'Series'} class:collection={hit.kind === 'Collection'}>
                        {hit.kind === 'Series' ? 'SERIES' : hit.kind === 'Collection' ? 'COLLECTION' : 'FILM'}
                    </span>
                    <span class="verb">{already(hit) ? 'added' : '+'}</span>
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

    .playlist-row { padding-top: 0; }

    .add-playlist {
        flex: 0 0 auto;
        background: rgba(255, 255, 255, .05);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 7px 12px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text-body);
        cursor: pointer;
    }

    .add-playlist:disabled { opacity: .45; cursor: default; }

    .playlist-note {
        margin: 6px 0 0;
        font-size: 12px;
        color: var(--lt-text-muted);
    }

    .playlist-note.bad { color: #e08585; }

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

    .verb { flex: 0 0 auto; font-size: 12px; color: var(--lt-text-dim); }

    .none, .bad { padding: 12px 13px; margin: 0; font-size: 12.5px; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
