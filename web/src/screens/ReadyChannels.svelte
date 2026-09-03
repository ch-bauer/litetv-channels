<script lang="ts">
    import { store } from '../lib/config.svelte';
    import { failureWords } from '../lib/jellyfin';
    import { readyChannels, type ReadyChannelSuggestion } from '../lib/api/suggestions';
    import type { ChannelSource, ProgramBlock } from '../lib/types';

    let { onDone }: { onDone: () => void } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    let suggestions = $state<ReadyChannelSuggestion[]>([]);
    let loading = $state(true);
    let error = $state<string | null>(null);

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
        channel.Artwork = {
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
            suggestions = await readyChannels();
        } catch (reason) {
            error = failureWords(reason);
        } finally {
            loading = false;
        }
    }

    void load();
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
        <button type="button" class="refresh" onclick={load} disabled={loading}>{german ? 'Neu prüfen' : 'Check again'}</button>
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
                    <div class="identity">
                        <p class="theme">{suggestion.Theme}</p>
                        <h3>{suggestion.Name}</h3>
                        <p class="description">{suggestion.Description}</p>
                    </div>
                    <div class="programme">
                        <div class="lineup">
                            <span>{suggestion.Sources.length} {german ? 'Quellen' : 'sources'}</span>
                            {#each suggestion.Features as feature (feature)}
                                <span class="feature">{feature}</span>
                            {/each}
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
                    </div>
                    <button type="button" class="add" onclick={() => add(suggestion)}>{german ? 'Kanal hinzufügen' : 'Add channel'}</button>
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
    .state, .problem { max-width: 880px; margin: 50px auto; color: var(--lt-text-dim); font-size: 13px; text-align: center; }
    .problem { color: #e08585; }
    .problem button { color: var(--lt-text-muted); }
    .strips { display: flex; flex-direction: column; gap: 10px; max-width: 880px; margin: 0 auto; }
    .strip { display: grid; grid-template-columns: 6px minmax(220px, 1.05fr) minmax(250px, 1fr) auto; gap: 18px; align-items: center; padding: 17px 18px 17px 0; border: 1px solid var(--lt-line); border-radius: var(--lt-radius); background: linear-gradient(90deg, rgba(119, 91, 244, .12), var(--lt-card) 22%, var(--lt-card)); overflow: hidden; }
    .signal { align-self: stretch; background: var(--lt-accent); box-shadow: 5px 0 20px var(--lt-accent-glow); }
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
    @media (max-width: 760px) { .intro { align-items: start; flex-direction: column; } .strip { grid-template-columns: 5px 1fr auto; } .programme { grid-column: 2 / 4; border-left: 0; border-top: 1px solid var(--lt-line); padding: 10px 0 0; } }
</style>
