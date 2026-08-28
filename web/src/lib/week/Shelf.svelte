<script lang="ts">
    /*
        The shelf: things to drag onto the week.

        The owner's words were that it is "just small and unusable with multiple links and such",
        that "the address below" is wrong because there is nothing below, and that dragging one
        tag is not what is wanted - "i want a list of entries to drag from with the shelf being
        larger".

        So it is a list, not a row of pills: search the library, get rows, drag any of them onto
        the grid. An address is one more kind of entry that goes on the shelf, added by the field
        at the top, rather than a separate control referring to a box that is not there.
    */
    import { search, type SearchHit } from '../search';
    import { api } from '../jellyfin';
    import { fetchPlaylist, looksLikeAddress, looksLikePlaylist, type PlaylistItem } from '../api/playlist';
    import { absolute } from '../jellyfin';

    export interface ShelfEntry {
        key: string;
        label: string;
        detail: string;
        itemId: string | null;
        url: string | null;
        /** A series can be opened to pick episodes out of. */
        seriesId?: string;
        /*
            A playlist can be opened too, and for the same reason.

            It used to be emptied onto the shelf: paste a playlist address and forty rows
            appeared. The owner asked for it to behave like a series instead - one row you can
            drag whole, or open and take one video out of - and they are right that those are
            the same thing. The videos are held here rather than re-fetched, because they were
            already read to find out how many there are.
        */
        playlist?: { url: string; items: PlaylistItem[] };
    }

    let { open = $bindable(true) }: { open?: boolean } = $props();

    let term = $state('');
    let hits = $state<SearchHit[]>([]);
    let busy = $state(false);
    let failed = $state<string | null>(null);
    let address = $state('');
    let extra = $state<ShelfEntry[]>([]);
    let timer: ReturnType<typeof setTimeout> | undefined;

    /*
        A series opened in the shelf. The owner asked for a season drop-down to pick episodes
        from: dragging "Miami Vice" onto a Tuesday is not what anyone means - they mean an
        episode of it. So a series row opens, the shelf shows that season's episodes, and the
        back button puts the search results back.
    */
    interface Season { Id: string; Name: string; IndexNumber?: number; }

    let opened = $state<{ id: string; name: string; playlist: boolean } | null>(null);
    let seasons = $state<Season[]>([]);
    let seasonId = $state<string | null>(null);
    let episodes = $state<ShelfEntry[]>([]);
    let openingError = $state<string | null>(null);

    const entries = $derived<ShelfEntry[]>(opened !== null ? episodes : [
        ...extra,
        ...hits.map((hit) => ({
            key: hit.id,
            label: hit.name,
            detail: hit.detail,
            itemId: hit.id,
            url: null,
            seriesId: hit.kind === 'Series' ? hit.id : undefined,
        })),
    ]);

    /** Opens a playlist the way a series opens: its own contents, and a way back. */
    function openPlaylist(entry: ShelfEntry): void {
        if (!entry.playlist) { return; }
        opened = { id: entry.playlist.url, name: entry.label, playlist: true };
        seasons = [];
        seasonId = null;
        openingError = null;
        episodes = entry.playlist.items.map((item) => ({
            key: 'yt:' + item.VideoId,
            label: item.Title,
            detail: item.Seconds > 0 ? Math.round(item.Seconds / 60) + ' min' : 'video',
            itemId: null,
            url: item.Url,
        }));
    }

    async function openSeries(entry: ShelfEntry): Promise<void> {
        if (entry.playlist) { openPlaylist(entry); return; }
        if (!entry.seriesId) { return; }
        opened = { id: entry.seriesId, name: entry.label, playlist: false };
        seasons = [];
        seasonId = null;
        episodes = [];
        openingError = null;
        try {
            const answer = await api().getItems<{ Items?: Season[] }>(api().getCurrentUserId(), {
                parentId: entry.seriesId,
                includeItemTypes: 'Season',
                sortBy: 'IndexNumber',
                sortOrder: 'Ascending',
                limit: 100,
            });
            seasons = answer.Items ?? [];
            // Straight into the first season: a drop-down that has to be used before anything
            // appears is a second click for no reason.
            if (seasons.length > 0) { await loadSeason(seasons[0].Id); }
        } catch (err) {
            openingError = err instanceof Error ? err.message : String(err);
        }
    }

    async function loadSeason(id: string): Promise<void> {
        seasonId = id;
        openingError = null;
        try {
            const answer = await api().getItems<{
                Items?: { Id: string; Name: string; IndexNumber?: number; RunTimeTicks?: number }[];
            }>(api().getCurrentUserId(), {
                parentId: id,
                includeItemTypes: 'Episode',
                sortBy: 'IndexNumber',
                sortOrder: 'Ascending',
                limit: 200,
                fields: 'RunTimeTicks',
            });
            episodes = (answer.Items ?? []).map((item) => ({
                key: 'ep:' + item.Id,
                label: (item.IndexNumber ? item.IndexNumber + '. ' : '') + item.Name,
                detail: item.RunTimeTicks
                    ? Math.round(item.RunTimeTicks / 600000000) + ' min'
                    : 'episode',
                itemId: item.Id,
                url: null,
            }));
        } catch (err) {
            openingError = err instanceof Error ? err.message : String(err);
        }
    }

    function closeSeries(): void {
        opened = null;
        seasons = [];
        seasonId = null;
        episodes = [];
        openingError = null;
    }

    async function run(): Promise<void> {
        const asked = term;
        busy = true;
        failed = null;
        try {
            const found = await search(asked, 40);
            if (asked !== term) { return; }
            hits = found;
        } catch (err) {
            failed = err instanceof Error ? err.message : String(err);
        } finally {
            busy = false;
        }
    }

    /*
        A link typed into the search box is a link, not a title. Detected rather than refused:
        the owner asked for exactly this, and searching the library for "https://youtube.com/..."
        can only ever answer "nothing matches", which reads as the search being broken.
    */
    const termIsAddress = $derived(looksLikeAddress(term));

    function onInput(): void {
        clearTimeout(timer);
        if (term.trim().length === 0 || termIsAddress) { hits = []; return; }
        timer = setTimeout(run, 250);
    }

    /** Takes what is in the search box as an address and puts it on the shelf. */
    async function addTypedAddress(): Promise<void> {
        address = term.trim();
        await addAddress();
        if (address.length === 0) { term = ''; }
    }

    let addressBusy = $state(false);
    let addressNote = $state<string | null>(null);

    /*
        An address goes on the shelf. A PLAYLIST address goes on the shelf as its videos, one
        entry each - the owner asked for exactly that, because a playlist as a single tag can
        only be dropped on the week whole, and that is not what anyone wants at eight o'clock on
        a Tuesday.
    */
    async function addAddress(): Promise<void> {
        const url = address.trim();
        if (url.length === 0) { return; }

        if (looksLikePlaylist(url)) {
            addressBusy = true;
            addressNote = null;
            try {
                const found = await fetchPlaylist(url);
                if (found.Items.length === 0) {
                    addressNote = 'YouTube gave nothing back for that playlist.';
                    return;
                }
                extra = [{
                    key: 'pl:' + url,
                    label: found.Items[0].Title + ' + ' + (found.Items.length - 1) + ' more',
                    detail: found.Items.length + ' videos - a playlist',
                    itemId: null,
                    url,
                    playlist: { url, items: found.Items },
                }, ...extra];
                address = '';
            } catch (err) {
                addressNote = 'That playlist could not be read: '
                    + (err instanceof Error ? err.message : String(err));
            } finally {
                addressBusy = false;
            }
            return;
        }

        extra = [{
            key: 'url:' + url,
            label: url.replace(/^https?:\/\//, '').slice(0, 60),
            detail: 'address',
            itemId: null,
            url,
        }, ...extra];
        address = '';
    }

    function onDragStart(event: DragEvent, entry: ShelfEntry): void {
        /*
            What the grid receives. An id or an address, and the name to draw before the server
            has answered - and, for a playlist dragged whole, the videos in it, so the screen
            can lay them out one after another instead of dropping a single row that stands for
            forty.
        */
        event.dataTransfer?.setData('text/plain', JSON.stringify({
            itemId: entry.itemId,
            url: entry.playlist ? null : entry.url,
            name: entry.label,
            playlist: entry.playlist?.items.map((item) => ({
                url: item.Url,
                name: item.Title,
                seconds: item.Seconds,
            })),
        }));
        if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'copy'; }
    }

    function poster(entry: ShelfEntry): string | null {
        return entry.itemId
            ? absolute('/Items/' + entry.itemId + '/Images/Primary?maxHeight=64&quality=90')
            : null;
    }
</script>

<section class="shelf" class:open>
    <header>
        <button type="button" class="toggle" onclick={() => (open = !open)} aria-expanded={open}>
            {open ? '▾' : '▸'} Shelf
        </button>
        <input
            class="find"
            type="search"
            bind:value={term}
            oninput={onInput}
            onkeydown={(e) => { if (e.key === 'Enter' && termIsAddress) { e.preventDefault(); addTypedAddress(); } }}
            placeholder="Search films, series and episodes…  (or paste a link)"
            aria-label="Search the library, or paste an address"
        />
        {#if termIsAddress}
            <button type="button" class="ghost" onclick={addTypedAddress} disabled={addressBusy}>
                {addressBusy ? 'Reading…' : 'That is a link — put it on the shelf'}
            </button>
        {/if}
        <input
            class="address"
            type="url"
            bind:value={address}
            onkeydown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addAddress(); } }}
            placeholder="…or paste an address"
            aria-label="Add an address to the shelf"
        />
        <button
            type="button"
            class="ghost"
            onclick={addAddress}
            disabled={addressBusy || address.trim().length === 0}
        >
            {addressBusy ? 'Reading the playlist...' : 'Put on the shelf'}
        </button>
        <span class="hint">drag onto the week · it snaps flush · hold Alt to drop on the second</span>
        {#if addressNote}<span class="hint bad">{addressNote}</span>{/if}
    </header>

    {#if open && opened}
        <div class="opened">
            <button type="button" class="ghost" onclick={closeSeries}>&lsaquo; Back to the search</button>
            <span class="opened-name" title={opened.name}>{opened.name}</span>
            {#if opened.playlist}
                <span class="hint">{episodes.length} videos - drag any of them onto the week</span>
            {:else if seasons.length > 0}
                <select
                    aria-label="Season"
                    value={seasonId}
                    onchange={(e) => loadSeason(e.currentTarget.value)}
                >
                    {#each seasons as season (season.Id)}
                        <option value={season.Id}>{season.Name}</option>
                    {/each}
                </select>
            {:else}
                <span class="hint">no seasons</span>
            {/if}
            {#if openingError}<span class="hint bad">{openingError}</span>{/if}
        </div>
    {/if}

    {#if open}
        <div class="entries">
            {#if failed}
                <p class="bad">That search failed: {failed}</p>
            {:else if entries.length === 0}
                <p class="none">
                    {busy ? 'Searching…' : 'Find something above, and it appears here to drag onto the week.'}
                </p>
            {:else}
                {#each entries as entry (entry.key)}
                    <div
                        class="entry"
                        draggable="true"
                        role="listitem"
                        ondragstart={(e) => onDragStart(e, entry)}
                    >
                        <svg class="grip" width="11" height="11" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                            <circle cx="9" cy="5" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="9" cy="19" r="1.6" />
                            <circle cx="15" cy="5" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="15" cy="19" r="1.6" />
                        </svg>
                        {#if poster(entry)}
                            <img src={poster(entry)} alt="" loading="lazy" />
                        {/if}
                        <span class="label" title={entry.label}>{entry.label}</span>
                        <span class="detail">{entry.detail}</span>
                        {#if entry.seriesId || entry.playlist}
                            <button
                                type="button"
                                class="episodes"
                                onclick={() => openSeries(entry)}
                                title={entry.playlist
                                    ? 'Pick one video out of this playlist'
                                    : 'Pick an episode out of this series'}
                            >{entry.playlist ? 'Videos' : 'Episodes'} &rsaquo;</button>
                        {/if}
                    </div>
                {/each}
            {/if}
        </div>
    {/if}
</section>

<style>
    .opened {
        display: flex;
        align-items: center;
        gap: 10px;
        margin-top: 9px;
        font-size: 12.5px;
    }

    .opened-name { font-weight: 600; color: var(--lt-text-title); }

    /* Colours come from the app-wide select rule in theme.css; only the size is local. */
    .opened select {
        font-size: 12.5px;
        padding: 4px 7px;
    }

    /*
        The way into a series' episodes, which is the whole reason a series is on the shelf at
        all - dropping "Miami Vice" on a Tuesday is not what anyone means. It was drawn in the
        dimmest text on the page inside the faintest border, and the owner's report was simply
        that it is too invisible. It is now the colour of a thing you are meant to press.
    */
    .episodes {
        flex: 0 0 auto;
        background: rgba(119, 91, 244, .16);
        border: 1px solid rgba(119, 91, 244, .45);
        border-radius: var(--lt-radius-small);
        color: #b6a9fa;
        font-family: inherit;
        font-size: 11.5px;
        font-weight: 600;
        padding: 4px 9px;
        cursor: pointer;
    }

    .episodes:hover { background: var(--lt-accent); border-color: var(--lt-accent); color: #fff; }

    .shelf {
        border-top: 1px solid var(--lt-line);
        padding: 10px 22px 12px;
        flex: 0 0 auto;
    }

    header {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
    }

    .toggle {
        background: none;
        border: none;
        font-size: 12.5px;
        font-weight: 700;
        font-family: inherit;
        color: var(--lt-text-title);
        cursor: pointer;
        padding: 0;
    }

    input {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 6px 10px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .find { flex: 1 1 250px; min-width: 12em; }
    .address { flex: 1 1 200px; min-width: 10em; }

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

    .ghost:disabled { opacity: .45; cursor: default; }

    .hint { font-size: 11.5px; color: var(--lt-text-dim); margin-left: auto; }

    /*
        Room to be used. The old shelf was one line high, which is why it read as an afterthought;
        this is a list you can actually pick from, and it scrolls rather than growing without end.

        The cap follows the window rather than sitting at a fixed 190px: once the app stopped
        collapsing to its floor there was room going spare, and the shelf is the part of this
        screen that was starved of it. It still cannot eat the grid whole - the clamp's ceiling
        sees to that - and it only reaches for the room when it has entries to show.
    */
    .entries {
        margin-top: 9px;
        max-height: clamp(190px, 30vh, 420px);
        overflow-y: auto;
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
    }

    .entry {
        display: flex;
        align-items: center;
        gap: 9px;
        padding: 7px 11px;
        border-bottom: 1px solid var(--lt-line-soft);
        cursor: grab;
    }

    .entry:hover { background: var(--lt-hover); }
    .entry:active { cursor: grabbing; }

    .grip { flex: 0 0 auto; color: rgba(255, 255, 255, .28); }

    img {
        flex: 0 0 24px;
        width: 24px;
        height: 32px;
        object-fit: cover;
        border-radius: 3px;
        background: var(--lt-field);
    }

    .label {
        flex-grow: 1;
        min-width: 0;
        font-size: 12.5px;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .detail { flex: 0 0 auto; font-size: 11px; color: var(--lt-text-dim); }

    .none, .bad { margin: 0; padding: 12px; font-size: 12.5px; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
