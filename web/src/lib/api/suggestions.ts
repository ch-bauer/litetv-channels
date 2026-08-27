/*
 * The scored pool behind the new-channel screens.
 *
 * `Engine` is on the wire and IS shown, always. That field is the only reason anyone noticed
 * suggestions had been running on the blunt scorer for weeks: a fallback that cannot be seen is
 * a fault that cannot be reported.
 */
import { api } from '../jellyfin';

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
