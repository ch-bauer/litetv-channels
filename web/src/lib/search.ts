/*
 * Finding something to add to a channel.
 *
 * The old page could not find a series at all - the owner's words, and the reason this is its
 * own file with its own types. Series are asked for **by name**, alongside films and box sets,
 * and `SearchTerm` is used rather than `NameStartsWith` so that "vice" finds "Miami Vice".
 */
import { api } from './jellyfin';
import type { ChannelSource, ChannelSourceType } from './types';

export interface SearchHit {
    id: string;
    name: string;
    kind: ChannelSourceType;
    detail: string;
    year?: number;
}

interface RawItem {
    Id: string;
    Name: string;
    Type: string;
    ProductionYear?: number;
    ChildCount?: number;
    RunTimeTicks?: number;
}

function kindOf(type: string): ChannelSourceType | null {
    if (type === 'Series') { return 'Series'; }
    if (type === 'BoxSet') { return 'Collection'; }
    if (type === 'Movie') { return 'Movie'; }
    return null;
}

function detailOf(item: RawItem, kind: ChannelSourceType): string {
    if (kind === 'Series') {
        return item.ChildCount ? 'series · ' + count(item.ChildCount, 'season') : 'series';
    }
    if (kind === 'Collection') {
        return item.ChildCount ? 'collection · ' + count(item.ChildCount, 'item') : 'collection';
    }
    const minutes = item.RunTimeTicks ? Math.round(item.RunTimeTicks / 600000000) : 0;
    const year = item.ProductionYear ? String(item.ProductionYear) : '';
    return [year, minutes ? minutes + ' min' : ''].filter(Boolean).join(' · ');
}

export async function search(term: string, limit = 20): Promise<SearchHit[]> {
    const trimmed = term.trim();
    if (trimmed.length === 0) { return []; }

    const answer = await api().getItems<{ Items?: RawItem[] }>(api().getCurrentUserId(), {
        searchTerm: trimmed,
        // All three, always. A channel is as likely to be built from a series or a box set as
        // from a film, and leaving one out is indistinguishable from the library being empty.
        includeItemTypes: 'Movie,Series,BoxSet',
        recursive: true,
        limit,
        fields: 'ChildCount,ProductionYear',
    });

    const hits: SearchHit[] = [];
    for (const item of answer.Items ?? []) {
        const kind = kindOf(item.Type);
        // Anything that is not one of the three kinds a channel can be built from is dropped
        // rather than guessed at.
        if (kind === null) { continue; }
        hits.push({
            id: item.Id,
            name: item.Name,
            kind,
            detail: detailOf(item, kind),
            year: item.ProductionYear,
        });
    }
    return hits;
}

export function toSource(hit: SearchHit): ChannelSource {
    return { Type: hit.kind, ItemId: hit.id, Name: hit.name };
}

/**
 * "1 season", "3 seasons". Written out because a series with one season read "1 seasons" on the
 * shelf, and a page that cannot count to one is not one anybody trusts with a schedule.
 */
export function count(howMany: number, noun: string): string {
    return howMany + ' ' + noun + (howMany === 1 ? '' : 's');
}
