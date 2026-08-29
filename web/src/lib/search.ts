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

export async function search(term: string, limit = 20): Promise<SearchHit[]> {
    const trimmed = term.trim();
    if (trimmed.length === 0) { return []; }

    const answer = await api().getItems<{ Items?: RawItem[] }>(api().getCurrentUserId(), {
        searchTerm: trimmed,
        // All four, always. A channel is as likely to be built from a series or a box set as
        // from a film, and leaving one out is indistinguishable from the library being empty.
        includeItemTypes: 'Movie,Series,BoxSet,Episode',
        recursive: true,
        limit,
        fields: 'ChildCount,ProductionYear',
    });

    const hits: SearchHit[] = [];
    for (const item of answer.Items ?? []) {
        const kind = kindOf(item.Type);
        // Anything that is not one of the kinds a channel can be built from is dropped rather
        // than guessed at.
        if (kind === null) { continue; }
        hits.push({
            id: item.Id,
            name: item.Name,
            kind,
            detail: detailOf(item, kind),
            year: item.ProductionYear,
            seriesId: item.SeriesId,
            openable: kind === 'Series' || kind === 'Collection',
        });
    }
    return hits;
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

export function toSource(hit: SearchHit): ChannelSource {
    if (hit.kind === 'Link') {
        // An address has no library item behind it, so it is stored as its URL and expanded
        // afresh every time a week is laid out.
        return { Type: 'YouTube', ItemId: '', Name: hit.name, Url: hit.url ?? '' };
    }
    return { Type: hit.kind, ItemId: hit.id, Name: hit.name };
}

/** The picture for a hit, if the library has one. A link has none. */
export function thumbFor(hit: SearchHit, width = 96): string | null {
    if (hit.id.length === 0) { return null; }
    return absolute('/Items/' + hit.id + '/Images/Primary?maxWidth=' + width);
}

/**
 * "1 season", "3 seasons". Written out because a series with one season read "1 seasons" on the
 * shelf, and a page that cannot count to one is not one anybody trusts with a schedule.
 */
export function count(howMany: number, noun: string): string {
    return howMany + ' ' + noun + (howMany === 1 ? '' : 's');
}
