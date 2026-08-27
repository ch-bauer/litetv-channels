<script lang="ts">
    /*
        A new channel, made from something rather than from nothing.

        Two halves, which are boards 7 and 8: start from a few titles you like, or start from a
        library and some genres. Both end the same way — a proposed lineup, a look at the evening
        it would make, and Create / Not that - where "not that" is about the lineup, and clears
        it rather than leaving the screen.

        **Nothing is saved until Save.** The channel is added to the configuration in the page and
        the screen says so, because a "Create" button that quietly writes to the server is how
        someone ends up with four half-made channels.
    */
    import Card from '../lib/ui/Card.svelte';
    import { store } from '../lib/config.svelte';
    import { api } from '../lib/jellyfin';
    import { search, type SearchHit } from '../lib/search';
    import { engineWords, scored, type ScoredSuggestions, type SuggestionMatch } from '../lib/api/suggestions';
    import type { ChannelSource } from '../lib/types';

    let { onDone, onBlank }: { onDone: () => void; onBlank: () => void } = $props();

    let half = $state<'titles' | 'library'>('titles');

    // --- from titles ---------------------------------------------------------------------
    let term = $state('');
    let hits = $state<SearchHit[]>([]);
    let seeds = $state<SearchHit[]>([]);
    let answer = $state<ScoredSuggestions | null>(null);
    let scoring = $state(false);
    let scoreError = $state<string | null>(null);
    let chosen = $state<Record<string, boolean>>({});

    async function find(): Promise<void> {
        if (term.trim().length === 0) { hits = []; return; }
        try { hits = await search(term, 10); } catch { hits = []; }
    }

    function addSeed(hit: SearchHit): void {
        if (seeds.some((s) => s.id === hit.id)) { return; }
        seeds = [...seeds, hit];
        term = '';
        hits = [];
        void rescore();
    }

    async function rescore(): Promise<void> {
        if (seeds.length === 0) { answer = null; return; }
        scoring = true;
        scoreError = null;
        try {
            answer = await scored(seeds.map((s) => s.id));
            chosen = {};
            for (const result of answer.Results.slice(0, 12)) { chosen[result.Id] = true; }
        } catch (err) {
            scoreError = err instanceof Error ? err.message : String(err);
        } finally {
            scoring = false;
        }
    }

    /** What the results have in common, tallied across the pool. */
    const common = $derived.by(() => {
        if (!answer) { return [] as { label: string; share: number }[]; }
        const pool = answer.Results;
        if (pool.length === 0) { return []; }
        const tally = new Map<string, number>();
        for (const result of pool) {
            for (const signal of [...result.SharedGenres, ...result.SharedPeople, ...result.SharedStudios]) {
                tally.set(signal, (tally.get(signal) ?? 0) + 1);
            }
        }
        return [...tally.entries()]
            .map(([label, n]) => ({ label, share: n / pool.length }))
            .sort((a, b) => b.share - a.share)
            .slice(0, 8);
    });

    // --- from a library and genres -------------------------------------------------------
    interface Folder { Id: string; Name: string; }
    interface LibItem { Id: string; Name: string; Type: string; Genres?: string[]; RunTimeTicks?: number; }

    let folders = $state<Folder[]>([]);
    let folderId = $state<string | null>(null);
    let items = $state<LibItem[]>([]);
    let loadingLibrary = $state(false);
    let pickedGenres = $state<string[]>([]);

    $effect(() => {
        if (half !== 'library' || folders.length > 0) { return; }
        api().getJSON<{ Items?: Folder[] }>(api().getUrl('Library/MediaFolders'))
            .then((a) => { folders = a.Items ?? []; })
            .catch(() => { folders = []; });
    });

    async function loadLibrary(id: string): Promise<void> {
        folderId = id;
        loadingLibrary = true;
        pickedGenres = [];
        try {
            const a = await api().getItems<{ Items?: LibItem[] }>(api().getCurrentUserId(), {
                parentId: id,
                includeItemTypes: 'Movie,Series',
                recursive: true,
                limit: 2000,
                fields: 'Genres,ParentId,RunTimeTicks',
            });
            items = a.Items ?? [];
        } finally {
            loadingLibrary = false;
        }
    }

    /*
        Counted here, not asked for: Jellyfin's /Genres says which genres exist and not how much
        is in them, and "Drama (3)" is a different decision from "Drama (410)".
    */
    const genreCounts = $derived.by(() => {
        const tally = new Map<string, number>();
        for (const item of items) {
            for (const genre of item.Genres ?? []) {
                tally.set(genre, (tally.get(genre) ?? 0) + 1);
            }
        }
        return [...tally.entries()].sort((a, b) => b[1] - a[1]).slice(0, 24);
    });

    /** Ticking two genres means titles in BOTH, which is the useful reading. */
    const matching = $derived.by(() => {
        if (pickedGenres.length === 0) { return items; }
        return items.filter((item) =>
            pickedGenres.every((genre) => (item.Genres ?? []).includes(genre)));
    });

    function toggleGenre(genre: string): void {
        pickedGenres = pickedGenres.includes(genre)
            ? pickedGenres.filter((g) => g !== genre)
            : [...pickedGenres, genre];
    }

    // --- what gets made ------------------------------------------------------------------
    const proposed = $derived.by<ChannelSource[]>(() => {
        if (half === 'titles') {
            if (!answer) { return []; }
            return answer.Results
                .filter((r) => chosen[r.Id])
                .map((r) => ({
                    Type: r.Kind === 'Series' ? 'Series' : 'Movie',
                    ItemId: r.Id,
                    Name: r.Name,
                } satisfies ChannelSource));
        }
        return matching.slice(0, 60).map((item) => ({
            Type: item.Type === 'Series' ? 'Series' : 'Movie',
            ItemId: item.Id,
            Name: item.Name,
        } satisfies ChannelSource));
    });

    /** A typical evening, laid from 20:00 on the items' real runtimes. */
    const evening = $derived.by(() => {
        const rows: { clock: string; label: string }[] = [];
        let minutes = 20 * 60;
        const source = half === 'titles' ? proposed.map((p) => ({ name: p.Name, minutes: 0 }))
            : matching.slice(0, 6).map((i) => ({
                name: i.Name,
                minutes: i.RunTimeTicks ? Math.round(i.RunTimeTicks / 600000000) : 0,
            }));
        for (const entry of source.slice(0, 6)) {
            const clock = String(Math.floor(minutes / 60) % 24).padStart(2, '0')
                + ':' + String(minutes % 60).padStart(2, '0');
            rows.push({ clock, label: entry.name });
            // 45 minutes where the server has no runtime, said rather than guessed at silently.
            minutes += Math.ceil((entry.minutes || 45) / 15) * 15;
        }
        return rows;
    });

    let name = $state('New channel');

    function create(): void {
        if (proposed.length === 0) { return; }
        store.addChannel(name, proposed);
        onDone();
    }

    /*
        "Not that" is an answer about the proposal, not about the screen: it stands beside
        Create, so it means "not this lineup". It used to leave for whichever channel happened
        to be selected, which read as the button dropping you somewhere at random.

        It now throws the proposal away and leaves you here to ask for another. Leaving is what
        the rail is for, and the hint under the buttons says so.
    */
    function notThat(): void {
        if (half === 'titles') {
            seeds = [];
            hits = [];
            term = '';
            answer = null;
            chosen = {};
            scoreError = null;
        } else {
            pickedGenres = [];
            folderId = null;
            items = [];
        }
        name = 'New channel';
    }

    function scoreOf(result: SuggestionMatch): string {
        return Math.round(result.Score) + '';
    }
</script>

<div class="screen">
    <header>
        <h1>A new channel</h1>
        <div class="spacer"></div>
        <button type="button" class="quiet" onclick={onBlank}>Start from nothing instead</button>
    </header>

    <nav class="halves">
        <button type="button" class:on={half === 'titles'} onclick={() => (half = 'titles')}>From titles I like</button>
        <button type="button" class:on={half === 'library'} onclick={() => (half = 'library')}>From a library and genres</button>
    </nav>

    <div class="body">
        <div class="left">
            {#if half === 'titles'}
                <Card>
                    <h3>Start from a few titles</h3>
                    <input
                        class="text"
                        bind:value={term}
                        oninput={find}
                        placeholder="Name a film or series…"
                        aria-label="Find a title to start from"
                    />
                    {#if hits.length > 0}
                        <div class="hits">
                            {#each hits as hit (hit.id)}
                                <button type="button" onclick={() => addSeed(hit)}>{hit.name} <span>{hit.detail}</span></button>
                            {/each}
                        </div>
                    {/if}

                    {#if seeds.length > 0}
                        <div class="seeds">
                            {#each seeds as seed (seed.id)}
                                <span class="seed">
                                    {seed.name}
                                    <button type="button" onclick={() => { seeds = seeds.filter((s) => s.id !== seed.id); void rescore(); }} aria-label="Remove {seed.name}">✕</button>
                                </span>
                            {/each}
                        </div>
                    {/if}
                </Card>

                {#if answer}
                    {@const words = engineWords(answer.Engine)}
                    <div class="engine" class:bad={!words.good}>{words.text}</div>
                {/if}

                {#if scoreError}
                    <p class="bad">{scoreError}</p>
                {:else if scoring}
                    <p class="none">Scoring…</p>
                {:else if answer}
                    <div class="results">
                        {#each answer.Results as result (result.Id)}
                            <label class="result">
                                <input
                                    type="checkbox"
                                    checked={!!chosen[result.Id]}
                                    onchange={(e) => (chosen[result.Id] = e.currentTarget.checked)}
                                />
                                <span class="score">{scoreOf(result)}</span>
                                <span class="rname">{result.Name}</span>
                                <span class="ryear">{result.Year ?? ''}</span>
                                <span class="rwhy">{result.SharedGenres.slice(0, 3).join(', ')}</span>
                            </label>
                        {/each}
                    </div>
                {/if}
            {:else}
                <Card>
                    <h3>Pick a library</h3>
                    <div class="folders">
                        {#each folders as folder (folder.Id)}
                            <button type="button" class:on={folderId === folder.Id} onclick={() => loadLibrary(folder.Id)}>
                                {folder.Name}
                            </button>
                        {:else}
                            <p class="none">No libraries found.</p>
                        {/each}
                    </div>
                </Card>

                {#if loadingLibrary}
                    <p class="none">Counting what is in there…</p>
                {:else if items.length > 0}
                    <Card>
                        <h3>And some genres</h3>
                        <p class="hint">Ticking two means titles in both.</p>
                        <div class="genres">
                            {#each genreCounts as [genre, count] (genre)}
                                <button type="button" class:on={pickedGenres.includes(genre)} onclick={() => toggleGenre(genre)}>
                                    {genre} <span>{count}</span>
                                </button>
                            {/each}
                        </div>
                        <p class="hint">{matching.length} titles match.</p>
                    </Card>
                {/if}
            {/if}
        </div>

        <div class="right">
            {#if half === 'titles' && common.length > 0}
                <Card>
                    <h3>What they have in common</h3>
                    <div class="common">
                        {#each common as signal (signal.label)}
                            <div class="crow">
                                <span class="clabel">{signal.label}</span>
                                <span class="cbar"><i style="width: {Math.round(signal.share * 100)}%"></i></span>
                                <span class="cshare">{Math.round(signal.share * 100)}%</span>
                            </div>
                        {/each}
                    </div>
                </Card>
            {/if}

            <Card>
                <h3>A typical evening</h3>
                {#if evening.length === 0}
                    <p class="none">Nothing chosen yet.</p>
                {:else}
                    <div class="evening">
                        {#each evening as row, index (index)}
                            <div class="erow">
                                <span class="at">{row.clock}</span>
                                <span class="bar"></span>
                                <span class="what" title={row.label}>{row.label}</span>
                            </div>
                        {/each}
                    </div>
                {/if}
            </Card>

            <Card>
                <h3>Make it</h3>
                <input class="text" bind:value={name} aria-label="Name for the new channel" />
                <p class="hint">{proposed.length} titles would go on it.</p>
                <div class="actions">
                    <button type="button" class="go" disabled={proposed.length === 0} onclick={create}>Create</button>
                    <button
                        type="button"
                        class="quiet"
                        onclick={notThat}
                        disabled={proposed.length === 0}
                        title="Throws this lineup away so you can ask for another"
                    >Not that</button>
                </div>
                <p class="hint warn">Nothing is written to the server until you press Save.</p>
                <p class="hint">Pick a channel in the rail to leave without making one.</p>
            </Card>
        </div>
    </div>
</div>

<style>
    .screen { flex-grow: 1; min-height: 0; display: flex; flex-direction: column; }

    header { display: flex; align-items: center; gap: 13px; padding: 16px 22px 0; }
    h1 { font-size: 21px; font-weight: 700; color: var(--lt-text-strong); margin: 0; }
    .spacer { flex-grow: 1; }

    .halves { display: flex; gap: 9px; padding: 13px 22px; border-bottom: 1px solid var(--lt-line); }

    .halves button {
        padding: 7px 14px;
        border-radius: var(--lt-radius-small);
        font-size: 13.5px;
        font-weight: 600;
        font-family: inherit;
        background: var(--lt-card);
        border: 1px solid var(--lt-line);
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .halves button.on {
        background: var(--lt-accent);
        border-color: var(--lt-accent);
        color: #fff;
        box-shadow: 0 4px 12px var(--lt-accent-glow);
    }

    .body { flex-grow: 1; min-height: 0; padding: 20px 22px; display: flex; gap: 26px; overflow: hidden; }
    .left { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 15px; overflow-y: auto; }
    .right { flex: 0 0 330px; display: flex; flex-direction: column; gap: 15px; overflow-y: auto; }

    h3 { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin: 0 0 9px; }

    .text {
        width: 100%;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .hits { margin-top: 8px; display: flex; flex-direction: column; }

    .hits button {
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

    .hits button span { color: var(--lt-text-dim); font-size: 11.5px; }
    .hits button:hover { color: var(--lt-text-title); }

    .seeds { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 10px; }

    .seed {
        display: inline-flex;
        align-items: center;
        gap: 7px;
        padding: 4px 10px;
        border-radius: 999px;
        background: rgba(119, 91, 244, .18);
        color: #b6a9fa;
        font-size: 12px;
    }

    .seed button { background: none; border: none; color: inherit; cursor: pointer; font-size: 10px; }

    .engine {
        padding: 8px 12px;
        border-radius: var(--lt-radius-small);
        background: rgba(47, 158, 143, .12);
        border-left: 2px solid #2f9e8f;
        font-size: 12.5px;
        color: var(--lt-text-muted);
    }

    .engine.bad { background: rgba(217, 154, 58, .1); border-left-color: var(--lt-collection); }

    .results { display: flex; flex-direction: column; border: 1px solid var(--lt-line); border-radius: var(--lt-radius); overflow: hidden; }

    .result {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 8px 12px;
        border-bottom: 1px solid var(--lt-line-soft);
        background: var(--lt-card);
        font-size: 12.5px;
        cursor: pointer;
    }

    .result:hover { background: var(--lt-hover); }

    .score { flex: 0 0 2.2em; font-weight: 700; color: var(--lt-accent); }
    .rname { flex-grow: 1; min-width: 0; color: var(--lt-text-title); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .ryear { flex: 0 0 auto; color: var(--lt-text-dim); font-size: 11.5px; }
    .rwhy { flex: 0 0 auto; max-width: 40%; color: var(--lt-text-dim); font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

    .folders, .genres { display: flex; gap: 6px; flex-wrap: wrap; }

    .folders button, .genres button {
        padding: 5px 11px;
        border-radius: 999px;
        border: 1px solid var(--lt-line-strong);
        background: none;
        font-size: 12px;
        font-family: inherit;
        color: var(--lt-text-muted);
        cursor: pointer;
    }

    .folders button.on, .genres button.on {
        background: var(--lt-accent);
        border-color: var(--lt-accent);
        color: #fff;
    }

    .genres button span { opacity: .65; font-size: 11px; }

    .hint { font-size: 12px; color: var(--lt-text-dim); margin: 9px 0 0; }
    .hint.warn { color: var(--lt-collection); }

    .common { display: flex; flex-direction: column; gap: 6px; }
    .crow { display: flex; align-items: center; gap: 9px; font-size: 12px; }
    .clabel { flex: 0 0 40%; color: var(--lt-text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .cbar { flex-grow: 1; height: 4px; border-radius: 2px; background: var(--lt-line-strong); overflow: hidden; }
    .cbar i { display: block; height: 100%; background: var(--lt-accent); }
    .cshare { flex: 0 0 auto; color: var(--lt-text-dim); font-size: 11px; }

    .evening { display: flex; flex-direction: column; gap: 7px; }
    .erow { display: flex; align-items: stretch; gap: 10px; }
    .at { flex: 0 0 42px; font-size: 12.5px; font-weight: 700; color: rgba(255, 255, 255, .7); }
    .bar { flex: 0 0 3px; border-radius: 2px; background: var(--lt-queue); min-height: 1.2em; }
    .what { flex-grow: 1; min-width: 0; font-size: 12.5px; color: var(--lt-text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

    .actions { display: flex; gap: 9px; margin-top: 11px; }

    .go {
        padding: 8px 16px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-accent);
        border: 1px solid var(--lt-accent);
        color: #fff;
        font-size: 13px;
        font-weight: 600;
        font-family: inherit;
        cursor: pointer;
    }

    .go:disabled { background: none; border-color: var(--lt-line-strong); color: var(--lt-text-faint); cursor: default; }

    .quiet {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        font-size: 12.5px;
        font-family: inherit;
        cursor: pointer;
        text-decoration: underline;
    }

    .none, .bad { font-size: 12.5px; margin: 0; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
