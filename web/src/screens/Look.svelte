<script lang="ts">
    /*
        Look: the three pictures a channel wears, the library's own artwork to pick from, and
        what it comes out looking like on the television.

        What the old page lost and the board keeps: a **Change** button per picture, a **preview
        of the television**, and **Borrow from a title** as its own card on the right rather than
        folded into the gallery.
    */
    import Card from '../lib/ui/Card.svelte';
    import Note from '../lib/ui/Note.svelte';
    import SectionTitle from '../lib/ui/SectionTitle.svelte';
    import { store } from '../lib/config.svelte';
    import { absolute, api, authHeaders, dashboard } from '../lib/jellyfin';
    import { search, type SearchHit } from '../lib/search';
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();

    type Slot = 'Banner' | 'Backdrop' | 'Poster';

    const SLOTS: { slot: Slot; name: string; ratio: string; height: number; flex: string }[] = [
        { slot: 'Banner', name: 'Banner', ratio: 'wide', height: 58, flex: '1.4 1 0' },
        { slot: 'Backdrop', name: 'Backdrop', ratio: '16:9', height: 84, flex: '1.2 1 0' },
        { slot: 'Poster', name: 'Poster', ratio: 'tall', height: 128, flex: '0.7 1 0' },
    ];

    type Shape = 'all' | 'wide' | 'tall' | 'square';

    let shape = $state<Shape>('wide');
    let tiles = $state<{ url: string; height: number }[]>([]);
    let loadingTiles = $state(false);
    let picking = $state<Slot | null>(null);
    let borrowTerm = $state('');
    let borrowHits = $state<SearchHit[]>([]);
    let uploading = $state(false);

    const artwork = $derived(channel.Artwork as Record<string, string | null | undefined>);

    function current(slot: Slot): string | null {
        const key = slot + 'Url';
        const value = artwork[key];
        return typeof value === 'string' && value.length > 0 ? absolute(value) : null;
    }

    function sourceOf(slot: Slot): string {
        if (current(slot)) { return 'set for this channel'; }
        const borrowed = artwork['ImageItemName'];
        if (typeof borrowed === 'string' && borrowed.length > 0) {
            return 'borrowed from ' + borrowed;
        }
        return 'whatever is on air';
    }

    /** The library's own pictures, from the titles this channel plays. */
    async function loadTiles(): Promise<void> {
        loadingTiles = true;
        try {
            const ids = channel.Sources.map((s) => s.ItemId).slice(0, 24);
            if (ids.length === 0) { tiles = []; return; }
            const kind = shape === 'tall' ? 'Primary' : shape === 'wide' ? 'Backdrop' : 'Thumb';
            tiles = ids.map((id) => ({
                url: absolute('/Items/' + id + '/Images/' + kind + '?maxHeight=240&quality=85'),
                height: shape === 'tall' ? 128 : shape === 'square' ? 84 : 62,
            }));
        } finally {
            loadingTiles = false;
        }
    }

    $effect(() => {
        void shape;
        void channel.Id;
        void loadTiles();
    });

    async function useTile(url: string): Promise<void> {
        if (!picking) { return; }
        const bar = dashboard();
        uploading = true;
        bar.showLoadingMsg();
        try {
            // Fetched to the server rather than linked: a picture chosen from elsewhere is kept
            // here so it cannot stop working later.
            await api().fetch({
                url: api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + picking + '/Fetch'),
                type: 'POST',
                data: JSON.stringify({ url }),
                contentType: 'application/json',
                dataType: 'json',
            });
            artwork[picking + 'Url'] = '/LiteTv/Artwork/' + channel.Id + '/' + picking + '?t=' + Date.now();
            store.touch();
            picking = null;
        } catch (err) {
            bar.alert('That picture could not be taken: ' + (err instanceof Error ? err.message : String(err)));
        } finally {
            uploading = false;
            bar.hideLoadingMsg();
        }
    }

    async function upload(slot: Slot, file: File): Promise<void> {
        const bar = dashboard();
        uploading = true;
        bar.showLoadingMsg();
        try {
            // Straight fetch, because an upload cannot go through ApiClient - which is the one
            // place the token has to be put on by hand.
            const answer = await fetch(api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + slot), {
                method: 'POST',
                headers: authHeaders(),
                body: file,
            });
            if (!answer.ok) { throw new Error(answer.status + ' ' + answer.statusText); }
            artwork[slot + 'Url'] = '/LiteTv/Artwork/' + channel.Id + '/' + slot + '?t=' + Date.now();
            store.touch();
        } catch (err) {
            bar.alert('That picture could not be uploaded: ' + (err instanceof Error ? err.message : String(err)));
        } finally {
            uploading = false;
            bar.hideLoadingMsg();
        }
    }

    function clearSlot(slot: Slot): void {
        artwork[slot + 'Url'] = null;
        store.touch();
    }

    async function findBorrow(): Promise<void> {
        if (borrowTerm.trim().length === 0) { borrowHits = []; return; }
        try {
            borrowHits = await search(borrowTerm, 8);
        } catch {
            borrowHits = [];
        }
    }

    function borrow(hit: SearchHit): void {
        artwork['ImageItemId'] = hit.id;
        artwork['ImageItemName'] = hit.name;
        store.touch();
        borrowTerm = '';
        borrowHits = [];
    }

    function stopBorrowing(): void {
        artwork['ImageItemId'] = null;
        artwork['ImageItemName'] = null;
        store.touch();
    }
</script>

<div class="screen">
    <div class="left">
        <div>
            <SectionTitle>The channel’s three pictures</SectionTitle>
            <div class="spaced">
                <Note>
                    Set none and the channel wears whatever is on air — which is fine for one built
                    from a single series, and a black rectangle for one built from a genre.
                </Note>
            </div>
        </div>

        <div class="slots">
            {#each SLOTS as entry (entry.slot)}
                <div class="slot" style="flex: {entry.flex}" class:picking={picking === entry.slot}>
                    <div class="slot-head">
                        <span class="slot-name">{entry.name}</span>
                        <span class="ratio">{entry.ratio}</span>
                    </div>

                    <div class="frame" style="height: {entry.height}px">
                        {#if current(entry.slot)}
                            <img src={current(entry.slot)} alt="" />
                        {/if}
                        <span class="caption">{channel.Name}</span>
                    </div>

                    <div class="source">{sourceOf(entry.slot)}</div>

                    <div class="slot-actions">
                        <button
                            type="button"
                            class="change"
                            onclick={() => (picking = picking === entry.slot ? null : entry.slot)}
                        >{picking === entry.slot ? 'Choosing…' : 'Change'}</button>

                        <label class="icon" title="Upload a picture">
                            <input
                                type="file"
                                accept="image/*"
                                onchange={(e) => {
                                    const file = e.currentTarget.files?.[0];
                                    if (file) { void upload(entry.slot, file); }
                                    e.currentTarget.value = '';
                                }}
                            />
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" aria-hidden="true">
                                <path d="M8 3v13a2 2 0 0 0 2 2h11" /><path d="M16 8H5a2 2 0 0 0-2 2v11" />
                            </svg>
                        </label>

                        {#if current(entry.slot)}
                            <button type="button" class="icon" title="Use whatever is on air again" onclick={() => clearSlot(entry.slot)}>✕</button>
                        {/if}
                    </div>
                </div>
            {/each}
        </div>

        <div class="gallery">
            <div class="gallery-head">
                <h3>Pictures your library already has</h3>
                <div class="filters">
                    {#each ['wide', 'tall', 'square'] as option (option)}
                        <button
                            type="button"
                            class="filter"
                            class:on={shape === option}
                            onclick={() => (shape = option as Shape)}
                        >{option}</button>
                    {/each}
                </div>
                <span class="tile-note">
                    {picking ? 'Pick one for the ' + picking.toLowerCase() : 'Press Change on a picture first'}
                </span>
            </div>

            <div class="tiles">
                {#each tiles as tile (tile.url)}
                    <button
                        type="button"
                        class="tile"
                        class:armed={picking !== null}
                        style="height: {tile.height}px"
                        disabled={picking === null || uploading}
                        onclick={() => useTile(tile.url)}
                    >
                        <img src={tile.url} alt="" loading="lazy" />
                    </button>
                {:else}
                    <p class="none">
                        {loadingTiles ? 'Looking…' : 'This channel has no content to take pictures from yet.'}
                    </p>
                {/each}
            </div>
        </div>
    </div>

    <div class="right">
        <Card>
            <h3>On the television</h3>
            <div class="tv">
                <div class="tv-hero">
                    {#if current('Backdrop')}<img src={current('Backdrop')} alt="" />{/if}
                    <div class="tv-text">
                        <div class="tv-name">{channel.Name}</div>
                        <div class="tv-now">what is on now</div>
                    </div>
                </div>
                <div class="tv-row">
                    {#each SLOTS as entry (entry.slot)}
                        <div class="tv-card">
                            {#if current(entry.slot)}<img src={current(entry.slot)} alt="" />{/if}
                        </div>
                    {/each}
                </div>
            </div>
            <p class="hint">The wide one is the row card; the tall one is for lists that show covers.</p>
        </Card>

        <Card>
            <h3>Borrow from a title instead</h3>
            <p class="hint tight">
                Name a film or series and the channel wears its artwork — and keeps following it,
                so re-scraping the series updates the channel too.
            </p>

            {#if artwork['ImageItemName']}
                <div class="borrowed">
                    <span>{artwork['ImageItemName']}</span>
                    <button type="button" onclick={stopBorrowing}>stop</button>
                </div>
            {/if}

            <input
                class="borrow"
                bind:value={borrowTerm}
                oninput={findBorrow}
                placeholder="Search a title…"
                aria-label="Borrow artwork from a title"
            />

            {#if borrowHits.length > 0}
                <div class="borrow-hits">
                    {#each borrowHits as hit (hit.id)}
                        <button type="button" onclick={() => borrow(hit)}>{hit.name}</button>
                    {/each}
                </div>
            {/if}
        </Card>

        <div class="footnote">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true">
                <circle cx="12" cy="12" r="9" /><path d="M12 8v5M12 16h.01" />
            </svg>
            A picture chosen from the web is downloaded and kept here, so it cannot stop working later.
        </div>
    </div>
</div>

<style>
    .screen {
        flex-grow: 1;
        min-height: 0;
        padding: 20px 22px;
        display: flex;
        gap: 26px;
        overflow: hidden;
    }

    .left { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 16px; overflow-y: auto; }
    .right { flex: 0 0 330px; display: flex; flex-direction: column; gap: 15px; overflow-y: auto; }

    .spaced { margin-top: 6px; }

    .slots { display: flex; gap: 14px; flex-wrap: wrap; }

    .slot {
        min-width: 150px;
        border: 1px solid rgba(255, 255, 255, .1);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        padding: 12px;
        display: flex;
        flex-direction: column;
        gap: 9px;
    }

    .slot.picking { border-color: var(--lt-accent); }

    .slot-head { display: flex; align-items: baseline; gap: 7px; }
    .slot-name { font-size: 13px; font-weight: 700; color: var(--lt-text-title); }
    .ratio { font-size: 11px; color: var(--lt-text-dim); }

    .frame {
        width: 100%;
        border-radius: 5px;
        background: linear-gradient(140deg, #33455e, #151d2a);
        position: relative;
        overflow: hidden;
        display: flex;
        align-items: flex-end;
        padding: 9px;
    }

    .frame img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
    }

    .caption {
        position: relative;
        font-size: 12px;
        font-weight: 700;
        color: #fff;
        text-shadow: 0 1px 4px rgba(0, 0, 0, .7);
    }

    .source { font-size: 11.5px; color: var(--lt-text-dim); }

    .slot-actions { display: flex; gap: 6px; }

    .change {
        flex: 1 1 0;
        text-align: center;
        padding: 6px 0;
        border-radius: 5px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        font-size: 11.5px;
        font-weight: 600;
        font-family: inherit;
        color: var(--lt-text-body);
        cursor: pointer;
    }

    .icon {
        flex: 0 0 auto;
        padding: 6px 9px;
        border-radius: 5px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        color: var(--lt-text-muted);
        cursor: pointer;
        font-size: 11px;
        font-family: inherit;
        display: inline-flex;
        align-items: center;
    }

    .icon input { display: none; }

    .gallery-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }

    h3 { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin: 0; }

    .filters { display: flex; gap: 5px; }

    .filter {
        padding: 4px 11px;
        border-radius: 999px;
        font-size: 11.5px;
        font-weight: 600;
        font-family: inherit;
        background: none;
        border: 1px solid var(--lt-line-strong);
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .filter.on { background: var(--lt-accent); border-color: var(--lt-accent); color: #fff; }

    .tile-note { margin-left: auto; font-size: 12px; color: var(--lt-text-dim); }

    .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(110px, 1fr));
        gap: 10px;
    }

    .tile {
        border: none;
        padding: 0;
        border-radius: 5px;
        overflow: hidden;
        background: var(--lt-field);
        cursor: pointer;
    }

    .tile:disabled { cursor: default; opacity: .75; }
    .tile.armed:hover { outline: 2px solid var(--lt-accent); }
    .tile img { width: 100%; height: 100%; object-fit: cover; display: block; }

    .tv { border-radius: var(--lt-radius-small); overflow: hidden; border: 1px solid var(--lt-line); margin-top: 10px; }

    .tv-hero {
        height: 120px;
        background: linear-gradient(140deg, #33455e, #151d2a);
        position: relative;
        display: flex;
        align-items: flex-end;
        padding: 11px;
    }

    .tv-hero img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
    .tv-text { position: relative; }
    .tv-name { font-size: 14px; font-weight: 700; color: #fff; }
    .tv-now { font-size: 11px; color: rgba(255, 255, 255, .7); margin-top: 2px; }

    .tv-row { display: flex; gap: 6px; padding: 9px; background: rgba(0, 0, 0, .25); }

    .tv-card {
        flex: 1 1 0;
        height: 34px;
        border-radius: 4px;
        overflow: hidden;
        background: linear-gradient(140deg, #2b3d55, #151d2a);
    }

    .tv-card img { width: 100%; height: 100%; object-fit: cover; }

    .hint { font-size: 12px; color: var(--lt-text-dim); margin: 9px 0 0; line-height: 1.5; }
    .hint.tight { margin: 6px 0 10px; color: var(--lt-text-muted); font-size: 12.5px; }

    .borrowed {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 12.5px;
        color: var(--lt-text-title);
        margin-bottom: 8px;
    }

    .borrowed button {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        font-family: inherit;
        font-size: 11.5px;
        cursor: pointer;
        text-decoration: underline;
    }

    .borrow {
        width: 100%;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .borrow-hits { margin-top: 7px; display: flex; flex-direction: column; }

    .borrow-hits button {
        text-align: left;
        background: none;
        border: none;
        border-bottom: 1px solid var(--lt-line-soft);
        padding: 7px 2px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text-muted);
        cursor: pointer;
    }

    .borrow-hits button:hover { color: var(--lt-text-title); }

    .footnote {
        display: flex;
        align-items: flex-start;
        gap: 9px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .footnote svg { flex: 0 0 auto; margin-top: 1px; }

    .none { grid-column: 1 / -1; margin: 0; font-size: 12.5px; color: var(--lt-text-dim); }
</style>
