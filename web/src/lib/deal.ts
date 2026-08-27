/*
 * "The first few, as they would fall".
 *
 * The design promises a preview of the dealt queue that answers to the Order and Interleave
 * controls beside it - including settings that have not been saved. The server cannot show
 * that, because the server has not been told yet, so the dealing is done here.
 *
 * It mirrors the server's rule rather than inventing one: each source expands to its playable
 * items (a film is itself, a series is its episodes in aired order, a collection is its
 * children), then sources are taken from in turn, `episodesPerBlock` at a time.
 *
 * **Shuffled means shuffled once, not re-drawn.** The server is emphatic about this - a schedule
 * that reshuffles promises one thing and airs another - so the shuffle here is seeded from the
 * channel id and gives the same answer every time it is asked.
 */
import { api } from './jellyfin';
import type { ChannelSource, PlayOrder } from './types';

export interface DealtItem {
    id: string;
    label: string;
    sourceIndex: number;
}

interface LibraryItem {
    Id: string;
    Name: string;
    IndexNumber?: number;
    ParentIndexNumber?: number;
    SeriesName?: string;
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
    if (source.Type === 'Movie') {
        return [{ id: source.ItemId, label: source.Name, sourceIndex: 0 }];
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

    const pools = await Promise.all(
        sources.map((s, i) => expand(s, want + 2).then((items) =>
            items.map((it) => ({ ...it, sourceIndex: i })))),
    );

    const take = Math.max(1, episodesPerBlock || 1);
    const queue: DealtItem[] = [];
    const cursors = pools.map(() => 0);

    if (order === 'Shuffle') {
        // Shuffle which source is drawn from next, not the items within a source: an episode
        // out of order is a different thing from a series out of order, and only the second is
        // what "shuffled" means here.
        const random = seeded(seed);
        let guard = 0;
        while (queue.length < want && guard++ < want * 20) {
            const live = pools.map((_, i) => i).filter((i) => cursors[i] < pools[i].length);
            if (live.length === 0) { break; }
            const pick = live[Math.floor(random() * live.length)];
            for (let n = 0; n < take && cursors[pick] < pools[pick].length && queue.length < want; n++) {
                queue.push(pools[pick][cursors[pick]++]);
            }
        }
        return queue;
    }

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
    return queue;
}
