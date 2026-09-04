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
    import { api, failureWords } from '../lib/jellyfin';
    import { franchiseSiblings, search, type FranchiseSibling, type SearchHit } from '../lib/search';
    import { engineWords, scored, type ScoredSuggestions, type SuggestionMatch } from '../lib/api/suggestions';
    import type { ChannelSource } from '../lib/types';
    import ReadyChannels from './ReadyChannels.svelte';

    let { onDone, onBlank }: { onDone: () => void; onBlank: () => void } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    let half = $state<'titles' | 'library' | 'ready'>('titles');

    // --- from titles ---------------------------------------------------------------------
    let term = $state('');
    let hits = $state<SearchHit[]>([]);
    let seeds = $state<SearchHit[]>([]);
    let collectionSeed = $state<SearchHit | null>(null);
    let answer = $state<ScoredSuggestions | null>(null);

    /*
        The similarity cut-off: show only what scores at least this.

        A wide continuous control rather than three preset bands - what counts as "similar
        enough" depends on the seeds, and a shortlist would be an answer disguised as a question.
        It runs the whole range the scorer can produce, and the reading beside it says what the
        number is doing to this pool rather than what it means in the abstract.
    */
    let cutoff = $state(0);
    let scoring = $state(false);
    let scoreError = $state<string | null>(null);
    let chosen = $state<Record<string, boolean>>({});

    /*
        What the cut-off took away.

        Raising it unticks whatever falls below, which is right - a channel must never be made
        from titles the screen is not showing. But the page then had no memory of it, so
        lowering the slider again brought the titles back UNTICKED, and the owner's report is
        exactly that: sliding around does not re-select what comes back into range. So a tick
        the cut-off removed is remembered here and given back when the title returns; a tick
        the owner removed by hand is not in here at all and stays off.
    */
    let hiddenTicks = $state<Record<string, boolean>>({});

    /*
        Which lineup this is. "Not that" asks for another one rather than clearing the screen -
        see `notThat` below.
    */
    let round = $state(0);

    /*
        Smart Similar's state, asked for only when the scoring falls back.

        The design's answer to "scored roughly" was an **Install it** offer, and the app had no
        equivalent. Asked rather than assumed, because a fallback has two causes that want
        opposite advice: the plugin is not there, which installing fixes, or it is there and did
        not answer - which installing does not fix, and which looks exactly the same from here.
        That distinction is the whole reason the Server screen draws three states, not two.
    */
    interface SmartSimilarState { Installed: boolean; Usable: boolean; Version: string | null; }
    let smartSimilar = $state<SmartSimilarState | null>(null);

    $effect(() => {
        if (answer?.Engine !== 'Rough') { return; }
        api().getJSON<{ Name: string; Installed: boolean; Usable: boolean; Version: string | null }[]>(
            api().getUrl('LiteTv/Plugins'),
        )
            .then((rows) => {
                const found = rows.find((row) => /smart similar/i.test(row.Name));
                smartSimilar = found
                    ? { Installed: found.Installed, Usable: found.Usable, Version: found.Version }
                    : { Installed: false, Usable: false, Version: null };
            })
            // The suggestions still work; this only decides which sentence to print.
            .catch(() => { smartSimilar = null; });
    });

    /** How many titles a proposal takes, and so how far each "not that" moves along. */
    const WINDOW = 12;

    /*
        Whether there is another lineup to show at all.

        "Not that" moves a window along a pool and wraps, so it can only offer something
        different when the pool is BIGGER than the window. On a small library it is not: five
        titles above the cut-off rotate back to the same five, every press, and the button reads
        as doing nothing. That is worth saying out loud rather than leaving the owner pressing
        it - and it says what to do about it, because both remedies are on this screen.
    */
    const anotherLineupExists = $derived.by(() => {
        if (half === 'titles') {
            if (collectionSeed) { return false; }
            return answer !== null && answer.Results.filter((r) => r.Score >= cutoff).length > WINDOW;
        }
        return matching.length > 60;
    });

    /*
        Look for a title to seed with.

        The answer is thrown away unless the box still says what was asked, which the shelf's
        search has always done and this one did not. Every keystroke starts a search, and they
        do not come back in order: typing "Avatar" into a library that has no Avatar showed a
        list of Fast & Furious films, because the answer to "A" arrived after the answer to
        "Avatar" and there was nothing to say it was stale. It reads as the search matching
        things it plainly does not match.
    */
    async function find(): Promise<void> {
        const asked = term;
        if (asked.trim().length === 0) { hits = []; return; }
        try {
            const found = await search(asked, 10);
            if (asked !== term) { return; }
            hits = found;
        } catch {
            if (asked === term) { hits = []; }
        }
    }

    /*
        A film's own sequels, offered as one-click chips beside the seeds rather than pulled in
        on their own - a seed is a deliberate choice, and choosing Spider-Man should not silently
        choose Spider-Man 2 and 3 with it.
    */
    let seedSiblings = $state<FranchiseSibling[]>([]);

    function addSeed(hit: SearchHit): void {
        if (hit.kind === 'Collection') {
            collectionSeed = hit;
            seeds = [];
            answer = null;
            chosen = {};
            hiddenTicks = {};
            scoreError = null;
            cutoff = 0;
            term = '';
            hits = [];
            seedSiblings = [];
            return;
        }
        if (collectionSeed) { collectionSeed = null; }
        if (seeds.some((s) => s.id === hit.id)) { return; }
        seeds = [...seeds, hit];
        term = '';
        hits = [];
        void rescore();

        if (hit.kind === 'Movie') {
            void franchiseSiblings(hit.id).then((found) => {
                seedSiblings = found.filter((sibling) => !seeds.some((s) => s.id === sibling.id));
            });
        } else {
            seedSiblings = [];
        }
    }

    function addSeedSibling(sibling: FranchiseSibling): void {
        addSeed({ id: sibling.id, name: sibling.name, kind: 'Movie', detail: sibling.year ? String(sibling.year) : '' });
        seedSiblings = seedSiblings.filter((candidate) => candidate.id !== sibling.id);
    }

    const kept = $derived(answer ? answer.Results.filter((r) => r.Score >= cutoff) : []);

    const cutoffWords = $derived.by(() => {
        if (!answer) { return ''; }
        const all = answer.Results.length;
        if (all === 0) { return 'nothing scored'; }
        const best = Math.round(Math.max(...answer.Results.map((r) => r.Score)));
        if (cutoff === 0) { return 'everything scored - ' + all + ' titles, best ' + best; }
        return kept.length + ' of ' + all + ' kept, best ' + best;
    });

    /*
        Raising the cut-off unticks what it hides. Otherwise the channel would quietly be made
        from titles the screen is no longer showing - which is the same class of fault as a
        control the server does not read.
    */
    function setCutoff(value: number): void {
        cutoff = value;
        if (!answer) { return; }
        for (const result of answer.Results) {
            if (result.Score < cutoff) {
                // Going out of range: remember whether it was ticked, then untick it.
                if (chosen[result.Id]) { hiddenTicks[result.Id] = true; }
                chosen[result.Id] = false;
            } else if (hiddenTicks[result.Id]) {
                // Coming back into range: it is ticked again, because that is how it was left.
                chosen[result.Id] = true;
                delete hiddenTicks[result.Id];
            }
        }
    }

    async function rescore(): Promise<void> {
        if (seeds.length === 0) { answer = null; return; }
        scoring = true;
        scoreError = null;
        try {
            answer = await scored(seeds.map((s) => s.id));
            round = 0;
            chosen = {};
            hiddenTicks = {};
            for (const result of answer.Results.slice(0, WINDOW)) { chosen[result.Id] = true; }
        } catch (err) {
            scoreError = failureWords(err);
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
    interface LibItem { Id: string; Name: string; Type: string; ChildCount?: number; Genres?: string[]; RunTimeTicks?: number; }

    let folders = $state<Folder[]>([]);
    let folderId = $state<string | null>(null);
    let items = $state<LibItem[]>([]);
    let collections = $state<LibItem[]>([]);
    let collectionTerm = $state('');
    let loadingLibrary = $state(false);
    let pickedGenres = $state<string[]>([]);
    let pickedCollection = $state<LibItem | null>(null);

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
        pickedCollection = null;
        collectionTerm = '';
        try {
            const [titles, boxes] = await Promise.all([
                api().getItems<{ Items?: LibItem[] }>(api().getCurrentUserId(), {
                    parentId: id,
                    includeItemTypes: 'Movie,Series',
                    recursive: true,
                    limit: 2000,
                    fields: 'Genres,ParentId,RunTimeTicks,ChildCount',
                }),
                api().getItems<{ Items?: LibItem[] }>(api().getCurrentUserId(), {
                    parentId: id,
                    includeItemTypes: 'BoxSet',
                    recursive: true,
                    sortBy: 'SortName,Name',
                    sortOrder: 'Ascending',
                    limit: 10000,
                    fields: 'ChildCount',
                }),
            ]);
            items = titles.Items ?? [];
            collections = boxes.Items ?? [];
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
        if (pickedCollection) { return []; }
        const titles = items.filter((item) => item.Type === 'Movie' || item.Type === 'Series');
        if (pickedGenres.length === 0) { return titles; }
        return titles.filter((item) =>
            pickedGenres.every((genre) => (item.Genres ?? []).includes(genre)));
    });

    const filteredCollections = $derived.by(() => {
        const query = collectionTerm.trim().toLocaleLowerCase();
        if (!query) { return collections; }
        return collections.filter((collection) => collection.Name.toLocaleLowerCase().includes(query));
    });

    function toggleGenre(genre: string): void {
        pickedGenres = pickedGenres.includes(genre)
            ? pickedGenres.filter((g) => g !== genre)
            : [...pickedGenres, genre];
    }

    // --- what gets made ------------------------------------------------------------------
    const proposed = $derived.by<ChannelSource[]>(() => {
        if (half === 'titles') {
            if (collectionSeed) {
                return [{ Type: 'Collection', ItemId: collectionSeed.id, Name: collectionSeed.name }];
            }
            if (!answer) { return []; }
            // The seeds are the titles the owner explicitly chose, so they belong in the
            // channel regardless of how the scorer ranks them. The old code only converted
            // answer.Results and silently dropped the chosen starting films and series.
            const startingTitles = seeds
                .filter((s): s is SearchHit & { kind: 'Movie' | 'Series' } =>
                    s.kind === 'Movie' || s.kind === 'Series')
                .map((s) => ({
                    Type: s.kind,
                    ItemId: s.id,
                    Name: s.name,
                } satisfies ChannelSource));
            const recommended = answer.Results
                .filter((r) => chosen[r.Id] && r.Score >= cutoff)
                .map((r) => ({
                    Type: r.Kind === 'Series' ? 'Series' : 'Movie',
                    ItemId: r.Id,
                    Name: r.Name,
                } satisfies ChannelSource));
            const seen = new Set<string>();
            return [...startingTitles, ...recommended].filter((source) => {
                if (seen.has(source.ItemId)) { return false; }
                seen.add(source.ItemId);
                return true;
            });
        }
        if (pickedCollection) {
            return [{ Type: 'Collection', ItemId: pickedCollection.Id, Name: pickedCollection.Name }];
        }
        /*
            A window into what matches, so "not that" can offer the next sixty rather than
            nothing at all. It wraps, so there is always another lineup to look at even when
            the genre holds fewer than a window's worth.
        */
        const size = 60;
        const start = matching.length === 0 ? 0 : (round * size) % matching.length;
        const window = [...matching.slice(start), ...matching.slice(0, start)].slice(0, size);
        return window.map((item) => ({
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
        "Not that" means NOT THIS LINEUP - so it offers the next one.

        It used to leave the screen, which read as the button dropping you somewhere at random;
        the port then made it clear every selection, and the owner's report is that it used to
        cycle round different options and no longer does. They are right, and cycling is the
        useful behaviour: the seeds and the genres were work, and throwing them away to be told
        "pick some titles" is not an answer to "not that one".

        So the seeds stay, the genres stay, and each press moves the window along: the next
        twelve scored titles, or the next sixty that match. Both wrap, so there is always
        another to look at. Starting over is its own button.
    */
    function notThat(): void {
        round += 1;

        if (half === 'titles' && answer) {
            const pool = answer.Results.filter((r) => r.Score >= cutoff);
            if (pool.length === 0) { return; }
            const start = (round * WINDOW) % pool.length;
            const window = [...pool.slice(start), ...pool.slice(0, start)].slice(0, WINDOW);
            chosen = {};
            hiddenTicks = {};
            for (const result of window) { chosen[result.Id] = true; }
        }
        // The library half needs nothing else: `proposed` reads `round` and takes the next
        // window of what matches.
    }

    /** Back to an empty screen, which is what "not that" used to do to everything. */
    function startOver(): void {
        round = 0;
        if (half === 'titles') {
            seeds = [];
            collectionSeed = null;
            hits = [];
            term = '';
            answer = null;
            chosen = {};
            hiddenTicks = {};
            scoreError = null;
            cutoff = 0;
            seedSiblings = [];
        } else {
            pickedGenres = [];
            pickedCollection = null;
            folderId = null;
            items = [];
            collections = [];
            collectionTerm = '';
        }
        name = 'New channel';
    }

    function scoreOf(result: SuggestionMatch): string {
        return Math.round(result.Score) + '';
    }
</script>

<div class="screen">
    <header>
        <h1>{german ? 'Neuer Kanal' : 'A new channel'}</h1>
        <div class="spacer"></div>
        <button type="button" class="quiet" onclick={onBlank}>{german ? 'Stattdessen ohne Inhalte starten' : 'Start from nothing instead'}</button>
    </header>

    <nav class="halves">
        <button type="button" class:on={half === 'titles'} onclick={() => (half = 'titles')}>{german ? 'Aus Lieblingstiteln' : 'From titles I like'}</button>
        <button type="button" class:on={half === 'library'} onclick={() => (half = 'library')}>{german ? 'Aus Bibliothek und Genres' : 'From a library and genres'}</button>
        <button type="button" class:on={half === 'ready'} onclick={() => (half = 'ready')}>{german ? 'Fertige Kanalvorschläge' : 'Ready channel ideas'}</button>
    </nav>

    {#if half === 'ready'}
        <ReadyChannels {onDone} />
    {:else}
    <div class="body">
        <div class="left">
            {#if half === 'titles'}
                <Card>
                    <h3>{german ? 'Mit einigen Titeln starten' : 'Start from a few titles'}</h3>
                    <input
                        class="text"
                        bind:value={term}
                        oninput={find}
                        placeholder={german ? 'Name eines Films, einer Serie oder Sammlung…' : 'Name a film, series or collection…'}
                        aria-label={german ? 'Film, Serie oder Sammlung zum Start suchen' : 'Find a film, series or collection to start from'}
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
                                    <button type="button" onclick={() => { seeds = seeds.filter((s) => s.id !== seed.id); seedSiblings = []; void rescore(); }} aria-label="Remove {seed.name}">✕</button>
                                </span>
                            {/each}
                        </div>
                    {/if}
                    {#if collectionSeed}
                        <div class="seeds">
                            <span class="seed collection-seed">
                                {collectionSeed.name} <small>{german ? 'Sammlung' : 'Collection'}</small>
                                <button type="button" onclick={() => { collectionSeed = null; }} aria-label="Remove {collectionSeed.name}">✕</button>
                            </span>
                        </div>
                    {/if}

                    {#if seedSiblings.length > 0}
                        <div class="siblings">
                            <span class="siblings-label">{german ? 'auch hinzufügen:' : 'also add:'}</span>
                            {#each seedSiblings as sibling (sibling.id)}
                                <button type="button" class="sibling" onclick={() => addSeedSibling(sibling)}>
                                    {sibling.name}{sibling.year ? ' (' + sibling.year + ')' : ''}
                                </button>
                            {/each}
                        </div>
                    {/if}
                </Card>

                {#if answer}
                    {@const words = engineWords(answer.Engine)}
                    <div class="engine" class:bad={!words.good}>
                        {words.text}
                        {#if answer.Engine === 'Rough' && smartSimilar}
                            {#if !smartSimilar.Installed}
                                &mdash; <a class="install" href="#/dashboard/plugins">{german ? 'Installieren' : 'Install it'}</a>
                                and these get sharper.
                            {:else if !smartSimilar.Usable}
                                &mdash; it <b>is</b> installed{smartSimilar.Version ? ' (' + smartSimilar.Version + ')' : ''}
                                but is not answering, so installing it again will not help. Look at it
                                under Plugins.
                            {/if}
                        {/if}
                    </div>
                {/if}

                {#if answer && answer.Results.length > 0}
                    <div class="cutoff">
                        <span class="cutoff-label">{german ? 'Nur Titel anzeigen mit mindestens' : 'Only show titles scoring at least'}</span>
                        <input
                            type="range"
                            min="0"
                            max="100"
                            step="1"
                            value={cutoff}
                            oninput={(e) => setCutoff(Number(e.currentTarget.value))}
                            aria-label="Similarity cut-off"
                        />
                        <span class="cutoff-value">{cutoff}</span>
                        <span class="cutoff-words">{cutoffWords}</span>
                    </div>
                {/if}

                {#if scoreError}
                    <p class="bad">{scoreError}</p>
                {:else if scoring}
                    <p class="none">{german ? 'Bewertung läuft…' : 'Scoring…'}</p>
                {:else if answer}
                    <div class="results">
                        {#each kept as result (result.Id)}
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
                        {:else}
                            <p class="none">
                                Nothing scores {cutoff} or more. The slider is above; the best
                                here is {Math.round(Math.max(0, ...answer.Results.map((r) => r.Score)))}.
                            </p>
                        {/each}
                    </div>
                {/if}
            {:else}
                <Card>
                    <h3>{german ? 'Bibliothek auswählen' : 'Pick a library'}</h3>
                    <div class="folders">
                        {#each folders as folder (folder.Id)}
                            <button type="button" class:on={folderId === folder.Id} onclick={() => loadLibrary(folder.Id)}>
                                {folder.Name}
                            </button>
                        {:else}
                            <p class="none">{german ? 'Keine Bibliotheken gefunden.' : 'No libraries found.'}</p>
                        {/each}
                    </div>
                </Card>

                {#if loadingLibrary}
                    <p class="none">{german ? 'Inhalte werden gezählt…' : 'Counting what is in there…'}</p>
                {:else if items.length > 0 || collections.length > 0}
                    <Card>
                        <h3>{german ? 'Und einige Genres' : 'And some genres'}</h3>
                        {#if collections.length > 0}
                            <p class="hint">{german ? 'Oder direkt eine Sammlung verwenden.' : 'Or use a collection directly.'}</p>
                            <input
                                class="text collection-search"
                                bind:value={collectionTerm}
                                placeholder={german ? 'Sammlungen durchsuchen…' : 'Search collections…'}
                                aria-label={german ? 'Sammlungen durchsuchen' : 'Search collections'}
                            />
                            <div class="genres collections">
                                {#each filteredCollections as collection (collection.Id)}
                                    <button type="button" class:on={pickedCollection?.Id === collection.Id} onclick={() => { pickedCollection = pickedCollection?.Id === collection.Id ? null : collection; pickedGenres = []; }}>
                                        {collection.Name} <span>{collection.ChildCount ?? 0}</span>
                                    </button>
                                {:else}
                                    <p class="none">{german ? 'Keine passende Sammlung.' : 'No matching collection.'}</p>
                                {/each}
                            </div>
                        {/if}
                        <p class="hint">{german ? 'Zwei Häkchen bedeuten: Titel aus beiden Genres.' : 'Ticking two means titles in both.'}</p>
                        <div class="genres" class:disabled={pickedCollection !== null}>
                            {#each genreCounts as [genre, count] (genre)}
                                <button type="button" disabled={pickedCollection !== null} class:on={pickedGenres.includes(genre)} onclick={() => toggleGenre(genre)}>
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
                    <h3>{german ? 'Was sie gemeinsam haben' : 'What they have in common'}</h3>
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
                <h3>{german ? 'Ein typischer Abend' : 'A typical evening'}</h3>
                {#if evening.length === 0}
                    <p class="none">{german ? 'Noch nichts ausgewählt.' : 'Nothing chosen yet.'}</p>
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
                <h3>{german ? 'Erstellen' : 'Make it'}</h3>
                <input class="text" bind:value={name} aria-label="Name for the new channel" />
                <p class="hint">{proposed.length} titles would go on it.</p>
                <div class="actions">
                    <button type="button" class="go" disabled={proposed.length === 0} onclick={create}>{german ? 'Erstellen' : 'Create'}</button>
                    <button
                        type="button"
                        class="quiet"
                        onclick={notThat}
                        disabled={proposed.length === 0 || !anotherLineupExists}
                        title={anotherLineupExists
                            ? 'Keeps what you told it and offers the next lineup'
                            : 'There is no other lineup to offer: everything that qualifies is already on this one.'}
                    >{german ? 'Nicht das — andere Vorschläge' : 'Not that — show me another'}</button>
                    <button
                        type="button"
                        class="quiet"
                        onclick={startOver}
                        title="Clears the seeds and the genres and starts again"
                    >{german ? 'Neu beginnen' : 'Start over'}</button>
                </div>
                {#if !anotherLineupExists && proposed.length > 0}
                    <p class="hint">
                        This is every title that qualifies, so there is no other lineup to offer.
                        {#if half === 'titles'}
                            Lower the cut-off, or add another title to start from.
                        {:else}
                            Choose another genre, or add one.
                        {/if}
                    </p>
                {/if}
                {#if round > 0}
                    <p class="hint">Lineup {round + 1}. The titles you started from are kept.</p>
                {/if}
                <p class="hint warn">The new channel is saved automatically.</p>
                <p class="hint">Pick a channel in the rail to leave without making one.</p>
            </Card>
        </div>
    </div>
    {/if}
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

    .siblings { display: flex; align-items: center; gap: 7px; flex-wrap: wrap; margin-top: 10px; }
    .siblings-label { font-size: 11.5px; color: var(--lt-text-dim); }

    .sibling {
        padding: 4px 9px;
        border-radius: 999px;
        border: 1px solid var(--lt-line-strong);
        background: none;
        color: var(--lt-accent);
        font-size: 12px;
        font-family: inherit;
        cursor: pointer;
    }

    .sibling:hover { background: var(--lt-hover); }

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
    .collection-seed small { opacity: .75; font-size: 10px; }
    .collection-search { margin: 8px 0; }

    .engine {
        padding: 8px 12px;
        border-radius: var(--lt-radius-small);
        background: rgba(47, 158, 143, .12);
        border-left: 2px solid #2f9e8f;
        font-size: 12.5px;
        color: var(--lt-text-muted);
    }

    .engine .install { color: inherit; text-decoration: underline; }
    .engine.bad { background: rgba(217, 154, 58, .1); border-left-color: var(--lt-collection); }

    .cutoff {
        display: flex;
        align-items: center;
        gap: 11px;
        flex-wrap: wrap;
        font-size: 12.5px;
        color: var(--lt-text-muted);
    }

    .cutoff input { flex: 1 1 180px; min-width: 120px; accent-color: var(--lt-accent); }

    .cutoff-label { flex: 0 0 auto; }

    .cutoff-value {
        flex: 0 0 auto;
        min-width: 26px;
        font-weight: 700;
        color: var(--lt-text-title);
        font-variant-numeric: tabular-nums;
    }

    .cutoff-words { flex: 0 0 auto; color: var(--lt-text-dim); font-size: 11.5px; }

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
