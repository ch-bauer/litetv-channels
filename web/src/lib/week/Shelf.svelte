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
    import { absolute } from '../jellyfin';

    export interface ShelfEntry {
        key: string;
        label: string;
        detail: string;
        itemId: string | null;
        url: string | null;
    }

    let { open = $bindable(true) }: { open?: boolean } = $props();

    let term = $state('');
    let hits = $state<SearchHit[]>([]);
    let busy = $state(false);
    let failed = $state<string | null>(null);
    let address = $state('');
    let extra = $state<ShelfEntry[]>([]);
    let timer: ReturnType<typeof setTimeout> | undefined;

    const entries = $derived<ShelfEntry[]>([
        ...extra,
        ...hits.map((hit) => ({
            key: hit.id,
            label: hit.name,
            detail: hit.detail,
            itemId: hit.id,
            url: null,
        })),
    ]);

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

    function onInput(): void {
        clearTimeout(timer);
        if (term.trim().length === 0) { hits = []; return; }
        timer = setTimeout(run, 250);
    }

    function addAddress(): void {
        const url = address.trim();
        if (url.length === 0) { return; }
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
        // What the grid receives. An id or an address, and the name to draw before the server
        // has answered.
        event.dataTransfer?.setData('text/plain', JSON.stringify({
            itemId: entry.itemId,
            url: entry.url,
            name: entry.label,
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
            placeholder="Search films, series and episodes…"
            aria-label="Search the library for something to drag onto the week"
        />
        <input
            class="address"
            type="url"
            bind:value={address}
            onkeydown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addAddress(); } }}
            placeholder="…or paste an address"
            aria-label="Add an address to the shelf"
        />
        <button type="button" class="ghost" onclick={addAddress} disabled={address.trim().length === 0}>
            Put on the shelf
        </button>
        <span class="hint">drag onto the week · hold Alt to drop on the second</span>
    </header>

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
                    </div>
                {/each}
            {/if}
        </div>
    {/if}
</section>

<style>
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
    */
    .entries {
        margin-top: 9px;
        max-height: 190px;
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
