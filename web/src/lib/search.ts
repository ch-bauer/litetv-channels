/*
 * Finding something to put on a channel - one search, used everywhere.
 *
 * The old page could not find a series at all - the owner's words, and the reason this is its
 * own file with its own types. Series are asked for **by name**, alongside films and box sets,
 * and `SearchTerm` is used rather than `NameStartsWith` so that "vice" finds "Miami Vice".
 *
 * It now finds **episodes** too, and it treats an **address** as a result rather than as a
 * different kind of input. Both came from the owner asking for "unified search bars everywhere
 * on the shelf and in the content section, which allow for films, series, episodes, collections
 * and links": every screen that looks for something to schedule should look for all five, in one
 * box, and there should be no second field beside it for the one kind that is not a title.
 */
import { api, absolute } from './jellyfin';
import { fetchPlaylist, looksLikeAddress, looksLikePlaylist } from './api/playlist';
import type { ChannelSource, ChannelSourceType } from './types';

/**
 * What a search can turn up.
 *
 * `Link` is not a [ChannelSourceType]: an address becomes a `YouTube` source when it is added,
 * but while it is a search result it is worth naming for what the viewer typed.
 */
export type FindKind = ChannelSourceType | 'Link';

export interface SearchHit {
    /** The library id, or the empty string for a link, which has no item behind it. */
    id: string;
    name: string;
    kind: FindKind;
    detail: string;
    year?: number;
    /** Set only for a link. */
    url?: string;
    /** The series an episode belongs to, so a row can say which one it is from. */
    seriesId?: string;
    /**
     * Whether this holds other things that could be taken out of it one at a time - a series,
     * a collection, a playlist. What a screen does with that is its own business: the shelf
     * opens it, the content list adds it whole.
     */
    openable?: boolean;
    /** For a link that turned out to be a playlist: how many videos, so a row can say so. */
    videoCount?: number;
    /**
     * A provider id ("tmdb:1234"), when the library has one. Two library items for the same
     * film - a plain copy and a separate "(4K)" one, most often - carry the same id here even
     * though nothing about their names says so; `search` uses this to show one of them rather
     * than both.
     */
    providerKey?: string;
}

interface RawItem {
    Id: string;
    Name: string;
    Type: string;
    ProductionYear?: number;
    ChildCount?: number;
    RunTimeTicks?: number;
    SeriesName?: string;
    SeriesId?: string;
    IndexNumber?: number;
    ParentIndexNumber?: number;
    ProviderIds?: Record<string, string>;
}

function kindOf(type: string): ChannelSourceType | null {
    if (type === 'Series') { return 'Series'; }
    if (type === 'BoxSet') { return 'Collection'; }
    if (type === 'Movie') { return 'Movie'; }
    if (type === 'Episode') { return 'Episode'; }
    return null;
}

/** "S02E07", or nothing at all when the library does not know where an episode sits. */
export function episodeNumber(item: { ParentIndexNumber?: number; IndexNumber?: number }): string {
    if (item.ParentIndexNumber === undefined || item.IndexNumber === undefined) { return ''; }
    const pad = (n: number) => (n < 10 ? '0' + n : String(n));
    return 'S' + pad(item.ParentIndexNumber) + 'E' + pad(item.IndexNumber);
}

function detailOf(item: RawItem, kind: ChannelSourceType): string {
    if (kind === 'Series') {
        return item.ChildCount ? 'series · ' + count(item.ChildCount, 'season') : 'series';
    }
    if (kind === 'Collection') {
        return item.ChildCount ? 'collection · ' + count(item.ChildCount, 'item') : 'collection';
    }

    const minutes = item.RunTimeTicks ? Math.round(item.RunTimeTicks / 600000000) : 0;
    const length = minutes ? minutes + ' min' : '';

    if (kind === 'Episode') {
        /*
            Which series, and where in it. An episode's own name is very often not enough to
            recognise it by - "Der Anfang" could be anything - and a list of episodes from four
            different series with nothing but their own titles is not searchable at all.
        */
        return [item.SeriesName ?? '', episodeNumber(item), length].filter(Boolean).join(' · ');
    }

    const year = item.ProductionYear ? String(item.ProductionYear) : '';
    return [year, length].filter(Boolean).join(' · ');
}

/** One request, mapped to rows. Anything that is not a kind a channel can be built from is
 * dropped rather than guessed at. */
async function ask(term: string, types: string, limit: number): Promise<SearchHit[]> {
    const answer = await api().getItems<{ Items?: RawItem[] }>(api().getCurrentUserId(), {
        searchTerm: term,
        includeItemTypes: types,
        recursive: true,
        limit,
        fields: 'ChildCount,ProductionYear,ProviderIds',
    });

    const hits: SearchHit[] = [];
    for (const item of answer.Items ?? []) {
        const kind = kindOf(item.Type);
        if (kind === null) { continue; }
        hits.push({
            id: item.Id,
            name: item.Name,
            kind,
            detail: detailOf(item, kind),
            year: item.ProductionYear,
            seriesId: item.SeriesId,
            openable: kind === 'Series' || kind === 'Collection',
            providerKey: providerKeyOf(item.ProviderIds),
        });
    }
    return hits;
}

/**
 * A stable key for "the same title", from whichever provider id the library happens to hold -
 * Tmdb first since it is what almost every film and series carries, Imdb as a fallback for the
 * rare item Tmdb never matched. Undefined when the library has neither, which is ordinary for a
 * title nobody has scraped; nothing is deduplicated on a guess.
 */
function providerKeyOf(ids?: Record<string, string>): string | undefined {
    if (!ids) { return undefined; }
    const tmdb = ids.Tmdb ?? ids.tmdb;
    if (tmdb) { return 'tmdb:' + tmdb; }
    const imdb = ids.Imdb ?? ids.imdb;
    return imdb ? 'imdb:' + imdb : undefined;
}

/**
 * Two library items for the same film - a plain copy and a separately-organised "(4K)" one,
 * most often - are two different search results with nothing in their names to say they are the
 * same title. Kept to one wherever the library itself agrees they are: the same provider id.
 * Anything without one (nothing scraped it) is left alone rather than guessed at from the name.
 */
function dedupeByProvider(hits: SearchHit[]): SearchHit[] {
    const seen = new Set<string>();
    return hits.filter((hit) => {
        if (!hit.providerKey) { return true; }
        const key = hit.kind + ':' + hit.providerKey;
        if (seen.has(key)) { return false; }
        seen.add(key);
        return true;
    });
}

/**
 * The tags a second copy of the same film is organised under, stripped so "Spider-Man" and
 * "Spider-Man (4K)" read as the same title. A known, narrow list rather than "anything in
 * parentheses" - a year, an edition nobody meant as a duplicate marker, or a genuinely different
 * film that happens to share a name must not collapse into this.
 */
const QUALITY_TAG_PATTERN = '[[(](4k|2160p|1080p|720p|hdr\\d*|dv|dolby ?vision|hd|sd|remux|bluray|blu-ray|web-?dl|uhd|extended|director\'?s cut|theatrical( cut)?|uncut|unrated)[\\])]';

/** A film's name with its quality/edition tags removed and whitespace tidied, for comparing two rows. */
function normalizedTitle(name: string): string {
    return name.replace(new RegExp(QUALITY_TAG_PATTERN, 'gi'), ' ').replace(/\s+/g, ' ').trim().toLowerCase();
}

/**
 * Whether a name carries one of the quality/edition tags itself, rather than being the plain
 * title. A fresh RegExp each time - `.test()` on a shared `g`-flagged pattern remembers where it
 * left off between calls and alternates true/false on repeated titles, which is not what a
 * membership check means.
 */
function hasQualityTag(name: string): boolean {
    return new RegExp(QUALITY_TAG_PATTERN, 'i').test(name);
}

/**
 * The fallback for when the library never gave the two copies a shared provider id at all - the
 * "(4K)" one is often imported and matched separately from the plain one, so their metadata can
 * disagree even though they are visibly the same film. Matched on the normalized name and, when
 * both carry one, the same year; kept to whichever copy's name carries none of the quality tags,
 * since that is the one somebody searching by title actually means.
 */
function dedupeByNormalizedTitle(hits: SearchHit[]): SearchHit[] {
    const groups = new Map<string, SearchHit[]>();
    const order: string[] = [];
    for (const hit of hits) {
        if (hit.kind !== 'Movie') { continue; }
        const key = normalizedTitle(hit.name) + '|' + (hit.year ?? '');
        if (!groups.has(key)) { groups.set(key, []); order.push(key); }
        groups.get(key)!.push(hit);
    }

    const drop = new Set<string>();
    for (const key of order) {
        const group = groups.get(key)!;
        if (group.length < 2) { continue; }
        const plain = group.find((hit) => !hasQualityTag(hit.name)) ?? group[0];
        for (const hit of group) {
            if (hit !== plain) { drop.add(hit.id); }
        }
    }

    return hits.filter((hit) => !drop.has(hit.id));
}

/**
 * What the library has that matches.
 *
 * **Two requests, not one, and the series come first.** Asking for all four kinds together let
 * the server decide the order, and it puts episodes wherever relevance lands them - so searching
 * a series by name listed its episodes above the series itself, and with a limit of twenty a
 * long-running show could fill the whole answer with episodes and never show the series at all.
 * That is the one row somebody searching "Simpsons" is looking for.
 *
 * Asking separately makes both true regardless of how many episodes match: the films, series and
 * collections are a list of their own, kept in the server's relevance order, and the episodes
 * follow after them.
 */
export async function search(term: string, limit = 20): Promise<SearchHit[]> {
    const trimmed = term.trim();
    if (trimmed.length === 0) { return []; }

    const [containers, episodes] = await Promise.all([
        ask(trimmed, 'Movie,Series,BoxSet', limit),
        ask(trimmed, 'Episode', limit),
    ]);

    return [...dedupeByNormalizedTitle(dedupeByProvider(containers)), ...episodes].slice(0, limit);
}

/**
 * An address, read and turned into the same kind of row a title produces.
 *
 * This is what lets one box take both. A playlist is read so the row can say how many videos
 * are in it - the same reason the content list reads one before adding it, since "YouTube
 * playlist" as a name tells nobody whether it holds four hundred videos or nothing at all. A
 * single video is taken at its address; naming it costs a second request that only the screens
 * which add it need to make.
 *
 * Never throws: a link that cannot be read is still a link, and refusing to show a row for it
 * would look exactly like the search being broken.
 */
export async function linkHit(url: string): Promise<SearchHit | null> {
    const trimmed = url.trim();
    if (!looksLikeAddress(trimmed)) { return null; }

    const hit: SearchHit = {
        id: '',
        name: trimmed,
        kind: 'Link',
        detail: 'address',
        url: trimmed,
    };

    if (!looksLikePlaylist(trimmed)) { return hit; }

    try {
        const found = await fetchPlaylist(trimmed);
        hit.name = found.Title && found.Title.length > 0 ? found.Title : trimmed;
        hit.videoCount = found.Items.length;
        hit.detail = 'playlist · ' + count(found.Items.length, 'video');
        hit.openable = found.Items.length > 0;
    } catch {
        // Read as a plain address instead. The screen adding it will say what went wrong when
        // it tries; a search result is not the place to report it.
        hit.detail = 'playlist';
    }

    return hit;
}

/**
 * The id a source carries when there is no library item behind it.
 *
 * `ChannelSource.ItemId` is a **Guid** on the server, and `System.Text.Json` cannot read an
 * empty string as one: the whole channel body then fails to bind and the save answers **400**
 * before any code of ours runs. So a link is written with the empty guid, spelled out, which is
 * what the page has always written and what `isEmptyId` on the way back already understands.
 */
const NO_ITEM = '00000000-0000-0000-0000-000000000000';

export function toSource(hit: SearchHit): ChannelSource {
    if (hit.kind === 'Link') {
        // An address has no library item behind it, so it is stored as its URL and expanded
        // afresh every time a week is laid out.
        return { Type: 'YouTube', ItemId: NO_ITEM, Name: hit.name, Url: hit.url ?? '' };
    }
    return { Type: hit.kind, ItemId: hit.id, Name: hit.name };
}

/** The picture for a hit, if the library has one. A link has none. */
export function thumbFor(hit: SearchHit, width = 96): string | null {
    if (hit.id.length === 0) { return null; }
    return absolute('/Items/' + hit.id + '/Images/Primary?maxWidth=' + width);
}

/** Another film in this hit's collection - "franchise", in the report that asked for it. */
export interface FranchiseSibling {
    id: string;
    name: string;
    year: number | null;
}

/**
 * The other films sharing a collection with a film already found or already on a channel - a
 * channel with Spider-Man 1 but not 2 or 3 was the report this answers. Empty for anything that
 * is not a film, or has no collection; a series or a link has neither.
 */
export function franchiseSiblings(itemId: string): Promise<FranchiseSibling[]> {
    return api()
        .getJSON<{ ItemId: string; Name: string; Year: number | null }[]>(api().getUrl('LiteTv/Franchise/' + itemId))
        .then((rows) => rows.map((row) => ({ id: row.ItemId, name: row.Name, year: row.Year })))
        .catch(() => []);
}

/**
 * "1 season", "3 seasons". Written out because a series with one season read "1 seasons" on the
 * shelf, and a page that cannot count to one is not one anybody trusts with a schedule.
 */
export function count(howMany: number, noun: string): string {
    return howMany + ' ' + noun + (howMany === 1 ? '' : 's');
}
