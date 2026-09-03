/*
 * The scored pool behind the new-channel screens.
 *
 * `Engine` is on the wire and IS shown, always. That field is the only reason anyone noticed
 * suggestions had been running on the blunt scorer for weeks: a fallback that cannot be seen is
 * a fault that cannot be reported.
 */
import { api } from '../jellyfin';
import type { ChannelSource, PlayOrder, ProgramBlock, TrailerMode } from '../types';

/** One proposed source, with what the preview before adding needs to name it. */
export interface SuggestedSource extends ChannelSource {
    Year?: number | null;
    Genres?: string[];
    /** Playable titles this source expands to; 1 for a film. */
    Titles?: number;
}

export interface ReadyChannelSuggestion {
    Name: string;
    Description: string;
    Theme: string;
    Features: string[];
    Sources: SuggestedSource[];
    EpisodesPerBlock: number;
    Order: PlayOrder;
    RandomizeEpisodes: boolean;
    Trailers: TrailerMode;
    TrailerEveryPrograms: number;
    TrailerLookahead: number;
    TrailersInGaps: boolean;
    MovieNight: Omit<ProgramBlock, 'Enabled' | 'DurationMinutes' | 'SameSourceProbability'> | null;
    Artwork: { ItemId: string; ItemName: string };
    Reason: SuggestionReason;
}

/**
 * Why a channel was proposed, and what adding it would cost.
 *
 * `EstimatedTitles` is the field the whole control panel exists for: a suggestion once expanded
 * to 453 titles and said nothing until it had been added.
 */
export interface SuggestionReason {
    /** studio, kids, factual, genre, film or collection. */
    Family: string;
    /** The audience band in words, already in German. */
    Audience: string;
    /** The studios or genres that selected these titles. */
    Because: string[];
    /** The libraries the titles came from. */
    Libraries: string[];
    SourceCount: number;
    /** Playable titles - episodes, not series. */
    EstimatedTitles: number;
    /** The size this proposal was held to. */
    SizeLimit: number;
    /** 'SmartSimilar', or 'Rough' when that plugin is absent or did not answer. */
    Engine: string;
}

export interface SuggestionLibrary {
    Id: string;
    Name: string;
    /** movies, tvshows, boxsets and so on, as the server names them. */
    Kind: string;
}

/** What the owner chose before the suggestions were built. */
export interface SuggestionControls {
    /** Empty means every library, which is the default. */
    libraries: string[];
    /** child, family, teen, adult, or '' for any. */
    audience: string;
    /** The largest schedule a proposal may expand to, in playable titles. */
    maxTitles: number;
    /** Empty means every family. */
    families: string[];
    /** Turn of the wheel: a higher number offers different ideas. */
    refresh: number;
    /** Names already said no to. */
    dismissed: string[];
    /** How tightly the titles must belong together, 0-100. The floor on the similarity score. */
    strictness: number;
    /** 'auto', 'on' or 'off'. A film channel never gets one whatever this says. */
    filmNight: string;
    /** Whether proposals come with the trailer preview turned on. */
    trailers: boolean;
    /** Whether a series' episodes are mixed before selection. */
    randomize: boolean;
    minSources: number;
    maxSources: number;
}

/** The libraries the suggestions can be drawn from. */
export function suggestionLibraries(): Promise<SuggestionLibrary[]> {
    return api().getJSON<SuggestionLibrary[]>(api().getUrl('LiteTv/Suggestions/Libraries'));
}

/** Finished local channel concepts: media, look and programme blocks included. */
export function readyChannels(controls?: SuggestionControls): Promise<ReadyChannelSuggestion[]> {
    const query: Record<string, string | number> = {};
    if (controls) {
        if (controls.libraries.length > 0) { query.libraries = controls.libraries.join(','); }
        if (controls.audience) { query.audience = controls.audience; }
        if (controls.families.length > 0) { query.families = controls.families.join(','); }
        if (controls.dismissed.length > 0) { query.dismissed = controls.dismissed.join(','); }
        query.maxTitles = controls.maxTitles;
        query.refresh = controls.refresh;
        query.strictness = controls.strictness;
        query.filmNight = controls.filmNight;
        query.trailers = String(controls.trailers);
        query.randomize = String(controls.randomize);
        query.minSources = controls.minSources;
        query.maxSources = controls.maxSources;
        // Smart Similar applies the asking account's library access, so it has to be told who.
        query.userId = api().getCurrentUserId();
    }

    return api().getJSON<ReadyChannelSuggestion[]>(api().getUrl('LiteTv/Suggestions', query));
}

export interface SiblingPluginStatus {
    Name: string;
    Installed: boolean;
    Usable: boolean;
    Version?: string | null;
    Note?: string | null;
}

export interface SuggestionSeed {
    Id: string;
    Name: string;
    Kind: string;
    /** False when this seed could not be scored at all. */
    Active: boolean;
    /** Local, Tmdb, Hybrid or Rough. */
    Source: string;
}

export interface SuggestionMatch {
    Id: string;
    Name: string;
    Kind: string;
    Year: number | null;
    CommunityRating: number | null;
    OfficialRating: string | null;
    /** Mean score over the comparable seeds, 0-100. */
    Score: number;
    PerSeed: (number | null)[];
    SharedGenres: string[];
    SharedTags: string[];
    SharedPeople: string[];
    SharedStudios: string[];
    YearGap: number | null;
    SameOfficialRating: boolean;
}

export interface ScoredSuggestions {
    /** "SmartSimilar", "Rough" when that plugin is absent or silent, or "None". */
    Engine: string;
    SmartSimilar: SiblingPluginStatus | null;
    Seeds: SuggestionSeed[];
    Results: SuggestionMatch[];
}

export function scored(itemIds: string[], limit = 40): Promise<ScoredSuggestions> {
    return api().getJSON<ScoredSuggestions>(api().getUrl('LiteTv/Suggestions/Scored', {
        itemIds: itemIds.join(','),
        userId: api().getCurrentUserId(),
        limit,
    }));
}

/** How the screen names the engine, in words rather than an identifier. */
export function engineWords(engine: string): { text: string; good: boolean } {
    if (engine === 'SmartSimilar') {
        return { text: 'Scored by Smart Similar', good: true };
    }
    if (engine === 'Rough') {
        return {
            text: 'Scored roughly — Smart Similar did not answer, so this is genre overlap only',
            good: false,
        };
    }
    return { text: 'Nothing scored yet', good: true };
}
