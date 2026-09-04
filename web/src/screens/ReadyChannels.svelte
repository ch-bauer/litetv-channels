<script lang="ts">
    import { store } from '../lib/config.svelte';
    import { failureWords } from '../lib/jellyfin';
    import {
        readyChannels,
        suggestionLibraries,
        suggestionThumb,
        type ReadyChannelSuggestion,
        type SuggestionControls,
        type SuggestionLibrary,
    } from '../lib/api/suggestions';
    import type { ChannelSource, ProgramBlock } from '../lib/types';

    let { onDone }: { onDone: () => void } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    let suggestions = $state<ReadyChannelSuggestion[]>([]);
    let loading = $state(true);
    let error = $state<string | null>(null);
    let libraries = $state<SuggestionLibrary[]>([]);

    /*
        The slider runs the whole useful range rather than offering three named sizes. A cap is
        the thing that stops a proposal quietly expanding to 453 titles, and where it should sit
        depends on the library behind it - a shortlist would be a guess presented as a question.

        Counted in playable titles, which is episodes rather than series: a channel of four
        series is not small when they are four long-running ones.
    */
    const SMALLEST = 5;
    const LARGEST = 500;

    let controls = $state<SuggestionControls>({
        libraries: [],
        audience: '',
        maxTitles: 60,
        families: [],
        refresh: 0,
        dismissed: [],
        strictness: 45,
        filmNight: 'auto',
        trailers: true,
        randomize: true,
        minSources: 2,
        maxSources: 30,
    });

    /** Which suggestion's title list is open. Only one at a time; they are long. */
    let opened = $state<string | null>(null);

    const filmNights = [
        { value: 'auto', de: 'wenn es passt', en: 'where it fits' },
        { value: 'on', de: 'immer', en: 'always' },
        { value: 'off', de: 'nie', en: 'never' },
    ];

    /** How the strictness reads at a glance, so the number is not the only clue. */
    function strictnessWords(value: number): string {
        if (value < 25) { return german ? 'weit gefasst' : 'broad'; }
        if (value < 55) { return german ? 'verwandt' : 'related'; }
        if (value < 80) { return german ? 'eng verwandt' : 'closely related'; }
        return german ? 'fast dasselbe' : 'almost the same';
    }

    function sourceWords(source: { Name: string; Year?: number | null; Type: string; Titles?: number }): string {
        const year = source.Year ? ' (' + source.Year + ')' : '';
        const kind = source.Type === 'Series'
            ? (german ? 'Serie' : 'series') + (source.Titles && source.Titles > 1
                ? ', ' + source.Titles + (german ? ' Folgen' : ' episodes')
                : '')
            : (german ? 'Film' : 'film');
        return source.Name + year + ' · ' + kind;
    }

    const audiences = [
        { value: '', de: 'Alle Altersgruppen', en: 'Any audience' },
        { value: 'child', de: 'Kinder', en: 'Children' },
        { value: 'family', de: 'Familie', en: 'Family' },
        { value: 'teen', de: 'Jugendliche', en: 'Teen' },
        { value: 'adult', de: 'Erwachsene', en: 'Adult' },
    ];

    const families = [
        { value: 'studio', de: 'Studio & Franchise', en: 'Studio & franchise' },
        { value: 'kids', de: 'Kinderprogramm', en: 'Children' },
        { value: 'factual', de: 'Doku & Fakten', en: 'Factual' },
        { value: 'genre', de: 'Genre-Sender', en: 'Genre' },
        { value: 'film', de: 'Filmkanal', en: 'Film' },
        { value: 'collection', de: 'Sammlungs-Marathon', en: 'Collection marathon' },
    ];

    function toggle(list: string[], value: string): string[] {
        return list.includes(value) ? list.filter((item) => item !== value) : [...list, value];
    }

    /** Say no to an idea: it goes, and it does not come back on the next turn of the wheel. */
    function dismiss(name: string): void {
        controls.dismissed = [...controls.dismissed, name];
        suggestions = suggestions.filter((candidate) => candidate.Name !== name);
    }

    function differentIdeas(): void {
        controls.refresh += 1;
        void load();
    }

    function source(source: ChannelSource): ChannelSource {
        return { Type: source.Type, ItemId: source.ItemId, Name: source.Name, Url: source.Url, Probability: source.Probability };
    }

    function add(suggestion: ReadyChannelSuggestion): void {
        store.addChannel(suggestion.Name, suggestion.Sources.map(source));
        const channel = store.config?.Channels.find((candidate) => candidate.Id === store.channelId);
        if (!channel) { return; }

        channel.EpisodesPerBlock = suggestion.EpisodesPerBlock;
        channel.Order = suggestion.Order;
        channel.RandomizeEpisodes = suggestion.RandomizeEpisodes;
        channel.Trailers = suggestion.Trailers;
        channel.TrailerEveryPrograms = suggestion.TrailerEveryPrograms;
        channel.TrailerLookahead = suggestion.TrailerLookahead;
        channel.TrailersInGaps = suggestion.TrailersInGaps;
        // A studio logo fetched from TMDb has no library item behind it - it goes on as a
        // direct address, the same way a hand-picked banner image does. Everything else keeps
        // borrowing a library item's own artwork, exactly as before.
        channel.Artwork = suggestion.Artwork.ExternalUrl
            ? {
                ImageItemId: '00000000-0000-0000-0000-000000000000',
                ImageItemName: suggestion.Artwork.ItemName,
                PosterUrl: suggestion.Artwork.ExternalUrl,
                BannerUrl: suggestion.Artwork.ExternalUrl,
            }
            : {
                ImageItemId: suggestion.Artwork.ItemId,
                ImageItemName: suggestion.Artwork.ItemName,
            };
        if (suggestion.MovieNight) {
            const block = suggestion.MovieNight;
            channel.Blocks = [{
                Name: block.Name,
                Enabled: true,
                StartMinutes: block.StartMinutes,
                DurationMinutes: 0,
                Days: block.Days,
                Sources: block.Sources.map(source),
                EpisodesPerBlock: block.EpisodesPerBlock,
                Order: block.Order,
                RandomizeEpisodes: block.RandomizeEpisodes,
                SameSourceProbability: 20,
                AdvanceOnePerWeek: block.AdvanceOnePerWeek,
                FitToContent: block.FitToContent,
                ShiftToAvoidLeadingGap: block.ShiftToAvoidLeadingGap,
                TrailerEnabled: block.TrailerEnabled,
                TrailerProgramsBefore: block.TrailerProgramsBefore,
            } satisfies ProgramBlock];
        }
        onDone();
    }

    async function load(): Promise<void> {
        loading = true;
        error = null;
        try {
            suggestions = await readyChannels(controls);
        } catch (reason) {
            error = failureWords(reason);
        } finally {
            loading = false;
        }
    }

    async function start(): Promise<void> {
        // The library list is not worth failing the screen over: with none of them known the
        // filter simply has nothing to offer and every library contributes, which is the default.
        try {
            libraries = await suggestionLibraries();
        } catch {
            libraries = [];
        }

        await load();
    }

    void start();
</script>

<div class="ready">
    <div class="intro">
        <div>
            <p class="eyebrow">{german ? 'AUS DEINER BIBLIOTHEK' : 'FROM YOUR LIBRARY'}</p>
            <h2>{german ? 'Fertige Kanäle, nicht nur Listen' : 'Finished channels, not just lists'}</h2>
            <p>{german
                ? 'Jeder Entwurf bringt seine lokalen Quellen, Gewichtung, Trailer-Vorschau und – wenn genug Filme passen – einen Filmabend gleich mit.'
                : 'Every concept includes local sources, weighting, trailer previews and — where enough films fit — a movie night.'}</p>
        </div>
        <button type="button" class="refresh" onclick={differentIdeas} disabled={loading}>
            {german ? 'Andere Ideen zeigen' : 'Show different ideas'}
        </button>
    </div>

    <div class="controls">
        {#if libraries.length > 1}
            <div class="control">
                <span class="label">{german ? 'Bibliotheken' : 'Libraries'}</span>
                <div class="chips">
                    <button type="button" class="chip" class:on={controls.libraries.length === 0}
                        onclick={() => { controls.libraries = []; void load(); }}>
                        {german ? 'Alle' : 'All'}
                    </button>
                    {#each libraries as library (library.Id)}
                        <button type="button" class="chip" class:on={controls.libraries.includes(library.Id)}
                            onclick={() => { controls.libraries = toggle(controls.libraries, library.Id); void load(); }}>
                            {library.Name}
                        </button>
                    {/each}
                </div>
            </div>
        {/if}

        <div class="control">
            <span class="label">{german ? 'Altersgruppe' : 'Audience'}</span>
            <div class="chips">
                {#each audiences as audience (audience.value)}
                    <button type="button" class="chip" class:on={controls.audience === audience.value}
                        onclick={() => { controls.audience = audience.value; void load(); }}>
                        {german ? audience.de : audience.en}
                    </button>
                {/each}
            </div>
        </div>

        <div class="control">
            <span class="label">{german ? 'Kanalgröße' : 'Channel size'}</span>
            <div class="size">
                <input type="range" min={SMALLEST} max={LARGEST} step="5"
                    bind:value={controls.maxTitles} onchange={() => void load()} />
                <output>{controls.maxTitles} {german ? 'Titel' : 'titles'}</output>
            </div>
            <p class="hint">
                {german
                    ? 'Höchstzahl abspielbarer Titel, also Folgen statt Serien. Ein Entwurf, der nicht hineinpasst, wird gar nicht erst angeboten.'
                    : 'The most playable titles a proposal may reach — episodes, not series. A concept that will not fit is not offered at all.'}
            </p>
        </div>

        <div class="control">
            <span class="label">{german ? 'Ähnlichkeit' : 'Similarity'}</span>
            <div class="size">
                <input type="range" min="0" max="100" step="5"
                    bind:value={controls.strictness} onchange={() => void load()} />
                <output>{strictnessWords(controls.strictness)}</output>
            </div>
            <p class="hint">
                {german
                    ? 'Wie eng die Titel zusammengehören müssen. Ein Studio allein macht keinen Sender: streng gestellt bleibt bei Animationsfilmen die Animation, und der Krimi desselben Studios fällt heraus.'
                    : 'How closely the titles must belong together. A studio alone is not a channel: set tight, animation stays with animation and the same studio\'s thriller drops out.'}
            </p>
        </div>

        <div class="control">
            <span class="label">{german ? 'Filmabend' : 'Film night'}</span>
            <div class="chips">
                {#each filmNights as choice (choice.value)}
                    <button type="button" class="chip" class:on={controls.filmNight === choice.value}
                        onclick={() => { controls.filmNight = choice.value; void load(); }}>
                        {german ? choice.de : choice.en}
                    </button>
                {/each}
            </div>
            <p class="hint">
                {german
                    ? 'Die Filme des Blocks sind nie zugleich der Kanalinhalt. Ein reiner Filmkanal bekommt gar keinen.'
                    : 'The block\'s films are never also the channel\'s content. A film channel gets none at all.'}
            </p>
        </div>

        <div class="control">
            <span class="label">{german ? 'Quellen je Kanal' : 'Sources per channel'}</span>
            <div class="size">
                <input type="range" min="1" max="40" step="1"
                    bind:value={controls.minSources} onchange={() => void load()} />
                <output>{german ? 'mindestens' : 'at least'} {controls.minSources}</output>
            </div>
            <div class="size">
                <input type="range" min={controls.minSources} max="80" step="1"
                    bind:value={controls.maxSources} onchange={() => void load()} />
                <output>{german ? 'höchstens' : 'at most'} {controls.maxSources}</output>
            </div>
            <p class="hint">
                {german
                    ? 'Eine Quelle ist ein Film oder eine ganze Serie, nie eine einzelne Folge — eine Serie bringt so viele Folgen mit, wie sie hat.'
                    : 'A source is one film or one whole series, never a single episode — a series brings along as many episodes as it has.'}
            </p>
        </div>

        <div class="control">
            <span class="label">{german ? 'Programm' : 'Programming'}</span>
            <div class="chips">
                <button type="button" class="chip" class:on={controls.trailers}
                    onclick={() => { controls.trailers = !controls.trailers; void load(); }}>
                    {german ? 'Trailer-Vorschau' : 'Trailer preview'}
                </button>
                <button type="button" class="chip" class:on={controls.randomize}
                    onclick={() => { controls.randomize = !controls.randomize; void load(); }}>
                    {german ? 'Serienfolgen mischen' : 'Shuffle episodes'}
                </button>
            </div>
        </div>

        <div class="control">
            <span class="label">{german ? 'Kanalarten' : 'Kinds of channel'}</span>
            <div class="chips">
                <button type="button" class="chip" class:on={controls.families.length === 0}
                    onclick={() => { controls.families = []; void load(); }}>
                    {german ? 'Alle' : 'All'}
                </button>
                {#each families as family (family.value)}
                    <button type="button" class="chip" class:on={controls.families.includes(family.value)}
                        onclick={() => { controls.families = toggle(controls.families, family.value); void load(); }}>
                        {german ? family.de : family.en}
                    </button>
                {/each}
            </div>
        </div>

        {#if controls.dismissed.length > 0}
            <div class="control">
                <span class="label">{german ? 'Abgelehnt' : 'Dismissed'}</span>
                <div class="chips">
                    {#each controls.dismissed as name (name)}
                        <button type="button" class="chip gone"
                            onclick={() => { controls.dismissed = controls.dismissed.filter((item) => item !== name); void load(); }}>
                            {name} ×
                        </button>
                    {/each}
                </div>
            </div>
        {/if}
    </div>

    {#if loading}
        <p class="state">{german ? 'Bibliothek wird zu Sendern zusammengesetzt…' : 'Turning the library into channels…'}</p>
    {:else if error}
        <div class="problem">
            <p>{german ? 'Die Vorschläge konnten nicht geladen werden:' : 'The suggestions could not load:'} {error}</p>
            <button type="button" onclick={load}>{german ? 'Erneut versuchen' : 'Try again'}</button>
        </div>
    {:else if suggestions.length === 0}
        <p class="state">{german ? 'Noch nicht genug passende Serien oder Filme für einen fertigen Sender. Du kannst die anderen beiden Wege oben weiterhin nutzen.' : 'There are not enough matching series or films for a finished channel yet. The other two ways above are still available.'}</p>
    {:else}
        <div class="strips">
            {#each suggestions as suggestion (suggestion.Name)}
                <article class="strip">
                    <div class="signal" aria-hidden="true"></div>
                    {#if suggestionThumb(suggestion.Artwork)}
                        <img class="thumb" src={suggestionThumb(suggestion.Artwork)} alt="" loading="lazy" />
                    {:else}
                        <div class="thumb placeholder" aria-hidden="true"></div>
                    {/if}
                    <div class="identity">
                        <p class="theme">{suggestion.Theme}</p>
                        <h3>{suggestion.Name}</h3>
                        <p class="description">{suggestion.Description}</p>
                    </div>
                    <div class="programme">
                        <div class="lineup">
                            <span>{suggestion.Sources.length} {german ? 'Quellen' : 'sources'}</span>
                            <span class="size-badge">
                                ~{suggestion.Reason.EstimatedTitles} {german ? 'Titel' : 'titles'}
                            </span>
                            {#each suggestion.Features as feature (feature)}
                                <span class="feature">{feature}</span>
                            {/each}
                        </div>
                        <div class="because">
                            <button type="button" class="peek"
                                onclick={() => (opened = opened === suggestion.Name ? null : suggestion.Name)}>
                                {opened === suggestion.Name
                                    ? (german ? 'Titel verbergen' : 'Hide titles')
                                    : (german ? 'Titel ansehen' : 'See titles')}
                            </button>
                            <span>{suggestion.Reason.Audience}</span>
                            {#each suggestion.Reason.Because as reason (reason)}
                                <span>{reason}</span>
                            {/each}
                            {#if suggestion.Reason.Libraries.length > 0}
                                <span>{suggestion.Reason.Libraries.join(', ')}</span>
                            {/if}
                            {#if suggestion.Reason.Engine === 'Rough'}
                                <span class="rough">
                                    {german
                                        ? 'grob sortiert — Smart Similar hat nicht geantwortet'
                                        : 'roughly sorted — Smart Similar did not answer'}
                                </span>
                            {/if}
                        </div>
                        {#if suggestion.MovieNight}
                            <div class="movie-night">
                                <b>{german ? 'SAMSTAG' : 'SATURDAY'}</b>
                                <span>20:15</span>
                                <span>{suggestion.MovieNight.Name}</span>
                                <i>{german ? 'startet ohne Leerlauf' : 'starts without a gap'}</i>
                            </div>
                        {:else}
                            <div class="movie-night muted">
                                <span>{german ? 'Durchgehend aus der lokalen Bibliothek' : 'Continuous local programming'}</span>
                            </div>
                        {/if}

                        {#if opened === suggestion.Name}
                            <ul class="titles">
                                {#each suggestion.Sources as source (source.ItemId)}
                                    <li>{sourceWords(source)}</li>
                                {/each}
                                {#if suggestion.MovieNight}
                                    <li class="block">{german ? 'Filmabend:' : 'Film night:'}</li>
                                    {#each suggestion.MovieNight.Sources as source (source.ItemId)}
                                        <li class="of-block">{sourceWords(source)}</li>
                                    {/each}
                                {/if}
                            </ul>
                        {/if}
                    </div>
                    <div class="decide">
                        <button type="button" class="add" onclick={() => add(suggestion)}>{german ? 'Kanal hinzufügen' : 'Add channel'}</button>
                        <button type="button" class="no" onclick={() => dismiss(suggestion.Name)}>
                            {german ? 'Nicht mehr zeigen' : 'Not this one'}
                        </button>
                    </div>
                </article>
            {/each}
        </div>
    {/if}
</div>

<style>
    .ready { flex-grow: 1; overflow-y: auto; padding: 28px 22px 36px; }
    .intro { display: flex; align-items: end; justify-content: space-between; gap: 26px; max-width: 880px; margin: 0 auto 25px; }
    .eyebrow { margin: 0 0 7px; color: var(--lt-accent); font-size: 10px; font-weight: 800; letter-spacing: .14em; }
    h2 { margin: 0; color: var(--lt-text-strong); font-size: 25px; letter-spacing: -.03em; }
    .intro > div > p:last-child { max-width: 670px; margin: 9px 0 0; color: var(--lt-text-dim); font-size: 13px; line-height: 1.48; }
    .refresh, .problem button { padding: 7px 11px; border: 1px solid var(--lt-line-strong); border-radius: var(--lt-radius-small); background: var(--lt-card); color: var(--lt-text-muted); font: 600 12px inherit; cursor: pointer; white-space: nowrap; }
    .refresh:disabled { opacity: .55; cursor: default; }
    .controls { display: flex; flex-direction: column; gap: 13px; max-width: 880px; margin: 0 auto 22px; padding: 15px 17px; border: 1px solid var(--lt-line); border-radius: var(--lt-radius); background: var(--lt-card); }
    .control { display: flex; flex-direction: column; gap: 7px; }
    .label { color: var(--lt-text-muted); font-size: 10px; font-weight: 800; letter-spacing: .1em; text-transform: uppercase; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; }
    .chip { padding: 5px 9px; border: 1px solid var(--lt-line-strong); border-radius: 99px; background: transparent; color: var(--lt-text-dim); font: 600 11.5px inherit; cursor: pointer; }
    .chip:hover { color: var(--lt-text-muted); }
    .chip.on { border-color: var(--lt-accent); background: var(--lt-accent); color: #fff; }
    .chip.gone { border-style: dashed; color: var(--lt-text-dim); }
    .size { display: flex; align-items: center; gap: 12px; }
    .size input { flex-grow: 1; max-width: 420px; accent-color: var(--lt-accent); }
    .size output { min-width: 88px; color: var(--lt-text-muted); font: 700 12px inherit; }
    .hint { margin: 0; color: var(--lt-text-dim); font-size: 11px; line-height: 1.4; }
    .size-badge { padding: 3px 6px; border: 1px solid var(--lt-queue); border-radius: 99px; color: var(--lt-queue); }
    .because { display: flex; flex-wrap: wrap; gap: 4px 10px; margin-top: 7px; color: var(--lt-text-dim); font-size: 10.5px; }
    .because span:not(:last-child)::after { content: ' ·'; }
    .peek { padding: 0; border: 0; background: none; color: var(--lt-accent); font: 600 10.5px inherit; cursor: pointer; text-decoration: underline; }
    .titles { display: flex; flex-direction: column; gap: 2px; margin: 8px 0 0; padding: 8px 0 0; border-top: 1px solid var(--lt-line); color: var(--lt-text-dim); font-size: 11px; list-style: none; }
    .titles .block { margin-top: 5px; color: var(--lt-collection); font-weight: 700; }
    .titles .of-block { padding-left: 10px; }
    .rough { color: #e0a85b; }
    .decide { display: flex; flex-direction: column; gap: 6px; }
    .no { padding: 6px 10px; border: 1px solid var(--lt-line-strong); border-radius: var(--lt-radius-small); background: transparent; color: var(--lt-text-dim); font: 600 11px inherit; cursor: pointer; white-space: nowrap; }
    .no:hover { color: var(--lt-text-muted); }
    .state, .problem { max-width: 880px; margin: 50px auto; color: var(--lt-text-dim); font-size: 13px; text-align: center; }
    .problem { color: #e08585; }
    .problem button { color: var(--lt-text-muted); }
    .strips { display: flex; flex-direction: column; gap: 10px; max-width: 880px; margin: 0 auto; }
    .strip { display: grid; grid-template-columns: 6px 64px minmax(220px, 1.05fr) minmax(250px, 1fr) auto; gap: 18px; align-items: center; padding: 17px 18px 17px 0; border: 1px solid var(--lt-line); border-radius: var(--lt-radius); background: linear-gradient(90deg, rgba(119, 91, 244, .12), var(--lt-card) 22%, var(--lt-card)); overflow: hidden; }
    .signal { align-self: stretch; background: var(--lt-accent); box-shadow: 5px 0 20px var(--lt-accent-glow); }
    .thumb { width: 64px; height: 64px; border-radius: var(--lt-radius-small); object-fit: cover; background: var(--lt-field); flex: 0 0 auto; }
    .thumb.placeholder { background: linear-gradient(135deg, var(--lt-field), var(--lt-line)); }
    .identity { min-width: 0; }
    .theme { margin: 0 0 4px; color: var(--lt-queue); font-size: 10px; font-weight: 800; letter-spacing: .1em; text-transform: uppercase; }
    h3 { margin: 0; color: var(--lt-text-title); font-size: 16px; }
    .description { margin: 5px 0 0; color: var(--lt-text-dim); font-size: 12px; line-height: 1.35; }
    .programme { min-width: 0; border-left: 1px solid var(--lt-line); padding-left: 18px; }
    .lineup { display: flex; flex-wrap: wrap; gap: 5px; color: var(--lt-text-dim); font-size: 11px; }
    .feature { padding: 3px 6px; border: 1px solid var(--lt-line-strong); border-radius: 99px; color: var(--lt-text-muted); }
    .movie-night { display: flex; align-items: center; gap: 8px; margin-top: 10px; color: var(--lt-text-muted); font-size: 11.5px; white-space: nowrap; }
    .movie-night b { color: var(--lt-collection); font-size: 10px; letter-spacing: .08em; }
    .movie-night i { overflow: hidden; color: var(--lt-text-dim); font-style: normal; text-overflow: ellipsis; }
    .movie-night.muted { color: var(--lt-text-dim); }
    .add { padding: 8px 12px; border: 1px solid var(--lt-accent); border-radius: var(--lt-radius-small); background: var(--lt-accent); color: #fff; font: 700 12px inherit; cursor: pointer; white-space: nowrap; }
    .add:hover { filter: brightness(1.08); }
    @media (max-width: 760px) { .intro { align-items: start; flex-direction: column; } .strip { grid-template-columns: 5px 48px 1fr auto; } .thumb { width: 48px; height: 48px; } .programme { grid-column: 3 / 5; border-left: 0; border-top: 1px solid var(--lt-line); padding: 10px 0 0; } }
</style>
