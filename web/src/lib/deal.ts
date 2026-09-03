/*
 * "The first few, as they would fall".
 *
 * The design promises a preview of the dealt queue that answers to the Order and Interleave
 * controls beside it - including settings that have not been saved. The server cannot show
 * that, because the server has not been told yet, so the dealing is done here.
 *
 * It mirrors the server's rule rather than inventing one: each source expands to its playable
 * items (a film is itself, a series is its episodes in aired order, a collection is its
 * children), then sources are taken from in turn, `episodesPerBlock` at a time. In the
 * source-aware shuffle mode each source is shuffled before that distribution happens.
 *
 * **Shuffled means shuffled once, not re-drawn.** The server is emphatic about this - a schedule
 * that reshuffles promises one thing and airs another - so the shuffle here is seeded from the
 * channel id and gives the same answer every time it is asked.
 */
import { api } from './jellyfin';
import { fetchPlaylist } from './api/playlist';
import type { ChannelSource, PlayOrder } from './types';

/**
 * Whether an id names nothing. Jellyfin writes guids without dashes in a plugin configuration
 * and with them elsewhere, so this compares the digits rather than the spelling - a literal
 * comparison is how "no artwork chosen" once read as "artwork chosen".
 */
function isEmptyId(id: string | null | undefined): boolean {
    return !id || id.replace(/-/g, '').replace(/0/g, '').length === 0;
}

export interface DealtItem {
    id: string;
    label: string;
    sourceIndex: number;
    sourceProbability?: number;
}

interface LibraryItem {
    Id: string;
    Name: string;
    Type?: string;
    IndexNumber?: number;
    ParentIndexNumber?: number;
    SeriesName?: string;
}

interface LinkedChild {
    ItemId?: string;
    ItemName?: string;
}

/** An episode reads as "Miami Vice - S02E14", which is how the design labels it. */
function episodeLabel(item: LibraryItem): string {
    const season = item.ParentIndexNumber;
    const episode = item.IndexNumber;
    const series = item.SeriesName ?? item.Name;
    if (season === undefined || episode === undefined) { return series + ' - ' + item.Name; }
    const pad = (n: number) => String(n).padStart(2, '0');
    return series + ' - S' + pad(season) + 'E' + pad(episode);
}

/**
 * What one source actually plays, in order. Capped: this is a preview of the first few, and
 * expanding a 111-episode series in full to show six lines would be absurd.
 */
async function expand(source: ChannelSource, cap: number): Promise<DealtItem[]> {
    if (source.Type === 'YouTube' && source.Url) {
        try {
            const playlist = await fetchPlaylist(source.Url);
            return playlist.Items.slice(0, cap).map((item) => ({
                id: item.VideoId || item.Url,
                label: item.Title,
                sourceIndex: 0,
            }));
        } catch {
            return [{ id: source.Url, label: source.Name || source.Url, sourceIndex: 0 }];
        }
    }

    /*
        A source with no library item behind it - a YouTube playlist - carries the all-zero id.
        Asking Jellyfin for `parentId=000...` is not an empty answer: `GetParentItem` hands it
        straight to `GetItemById`, which THROWS on an empty guid, and the request comes back 400
        with a stack trace in the server's log. It is the same trap the plugin's own lookups had.

        The playlist is expanded when the week is laid out, by the server, from the address - so
        the honest preview here is the source itself, named as it is on the list.
    */
    if (isEmptyId(source.ItemId)) {
        return [{ id: source.Url ?? source.Name, label: source.Name, sourceIndex: 0 }];
    }

    if (source.Type === 'Movie') {
        return [{ id: source.ItemId, label: source.Name, sourceIndex: 0 }];
    }

    if (source.Type === 'Collection') {
        /*
            A collection is not an alphabetic folder. Jellyfin stores the deliberate order in
            LinkedChildren, while an Items(parentId=...) query defaults to SortName. The latter
            made this preview disagree with the actual channel schedule, especially for film
            franchises such as Fast & Furious.

            Fetch linked children one by one so the order of the response cannot reorder them.
            The server's collection expansion does the same conceptually through
            GetLinkedChildren().
        */
        try {
            const collection = await api().getJSON<{ LinkedChildren?: LinkedChild[] }>(
                api().getUrl('Items/' + source.ItemId, { fields: 'LinkedChildren' }),
            );
            const linked = (collection.LinkedChildren ?? [])
                .filter((child) => Boolean(child.ItemId))
                .slice(0, cap);
            const children = await Promise.all(linked.map((child) =>
                api().getJSON<LibraryItem>(api().getUrl('Items/' + child.ItemId)),
            ));
            return children.map((item) => ({
                id: item.Id,
                label: item.Name,
                sourceIndex: 0,
            }));
        } catch {
            return [{ id: source.ItemId, label: source.Name, sourceIndex: 0 }];
        }
    }

    const query = source.Type === 'Series'
        ? {
            parentId: source.ItemId,
            includeItemTypes: 'Episode',
            recursive: true,
            sortBy: 'ParentIndexNumber,IndexNumber',
            sortOrder: 'Ascending',
            limit: cap,
            fields: 'ParentIndexNumber',
        }
        : {
            parentId: source.ItemId,
            recursive: false,
            sortBy: 'SortName',
            sortOrder: 'Ascending',
            limit: cap,
        };

    try {
        const answer = await api().getItems<{ Items?: LibraryItem[] }>(
            api().getCurrentUserId(),
            query,
        );
        return (answer.Items ?? []).map((item) => ({
            id: item.Id,
            label: source.Type === 'Series' ? episodeLabel({ ...item, SeriesName: source.Name }) : item.Name,
            sourceIndex: 0,
        }));
    } catch {
        // A source the library will not answer for still has a name, and showing it is better
        // than dropping it silently - the preview is meant to explain, not to hide.
        return [{ id: source.ItemId, label: source.Name, sourceIndex: 0 }];
    }
}

/** A small deterministic generator, so "shuffled" is stable for a given channel. */
function seeded(seed: string): () => number {
    let h = 2166136261;
    for (let i = 0; i < seed.length; i++) {
        h = Math.imul(h ^ seed.charCodeAt(i), 16777619);
    }
    return () => {
        h = Math.imul(h ^ (h >>> 15), 2246822507);
        h = Math.imul(h ^ (h >>> 13), 3266489909);
        return ((h ^= h >>> 16) >>> 0) / 4294967296;
    };
}

export async function deal(
    sources: ChannelSource[],
    order: PlayOrder,
    episodesPerBlock: number,
    seed: string,
    want = 6,
): Promise<DealtItem[]> {
    if (sources.length === 0) { return []; }

    const sourcePools = await Promise.all(
        sources.map((s, i) => expand(s, want + 2).then((items) =>
            items.map((it) => ({ ...it, sourceIndex: i })))),
    );

    const pools = order === 'ShuffleBySource'
        ? sourcePools.map((pool) => {
            const random = seeded(seed + ':' + pool[0]?.sourceIndex);
            const shuffled = [...pool];
            for (let i = shuffled.length - 1; i > 0; i--) {
                const j = Math.floor(random() * (i + 1));
                [shuffled[i], shuffled[j]] = [shuffled[j], shuffled[i]];
            }
            return shuffled;
        })
        : sourcePools;

    const take = Math.max(1, episodesPerBlock || 1);
    const queue: DealtItem[] = [];
    const cursors = pools.map(() => 0);

    // WeightedShuffle chooses a source for each block from its own configured weights. The first
    // episode remains the first configured episode; every later block gets a fresh lottery.
    if (order === 'WeightedShuffle') {
        const result: DealtItem[] = [];
        const weightedPools = pools.map((pool, index) => ({
            pool,
            index,
            cursor: 0,
            weight: Math.max(0, Math.min(100, sources[index].Probability ?? 100)),
        }));
        const first = weightedPools.find((candidate) => candidate.pool.length > 0);
        if (!first) { return []; }
        result.push(first.pool[first.cursor++]);
        const random = seeded(seed);
        while (result.length < want) {
            const available = weightedPools.filter((candidate) => candidate.cursor < candidate.pool.length);
            if (available.length === 0) { break; }
            const total = available.reduce((sum, candidate) => sum + candidate.weight, 0);
            let ticket = Math.floor(random() * (total > 0 ? total : available.length));
            let selected = available[available.length - 1];
            for (const candidate of available) {
                const weight = total > 0 ? candidate.weight : 1;
                if (ticket < weight) { selected = candidate; break; }
                ticket -= weight;
            }
            const count = episodesPerBlock <= 0
                ? selected.pool.length - selected.cursor
                : Math.min(take, selected.pool.length - selected.cursor);
            for (let i = 0; i < count && result.length < want; i++) {
                result.push(selected.pool[selected.cursor++]);
            }
        }
        return result;
    }

    // This is the same order as the server for the other modes: first interleave (or
    // concatenate), then apply the stable shuffle to the resulting queue.
    if (episodesPerBlock <= 0) {
        for (const pool of pools) { queue.push(...pool); }
        queue.length = Math.min(queue.length, want);
    } else {
        let guard = 0;
        while (queue.length < want && guard++ < want * 20) {
            let moved = false;
            for (let i = 0; i < pools.length && queue.length < want; i++) {
                for (let n = 0; n < take && cursors[i] < pools[i].length && queue.length < want; n++) {
                    queue.push(pools[i][cursors[i]++]);
                    moved = true;
                }
            }
            if (!moved) { break; }
        }
    }

    if (order === 'Shuffle') {
        const random = seeded(seed);
        for (let i = queue.length - 1; i > 0; i--) {
            const j = Math.floor(random() * (i + 1));
            [queue[i], queue[j]] = [queue[j], queue[i]];
        }
    }
    return queue;
}
