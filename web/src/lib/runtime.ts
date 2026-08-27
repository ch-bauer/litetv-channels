/*
 * How long a block's content actually runs.
 *
 * The owner's complaint was that a block's length is typed: "20:15 starts a Movie and the block
 * size is the movie size". So it is measured instead - from the library's own runtimes, the same
 * numbers the schedule builder uses.
 *
 * Two rules, and the screen says which one it applied, because a number with no account of where
 * it came from is how the last two bad controls got shipped:
 *
 *  - A film, or a collection, is as long as the things in it.
 *  - A series has no single length, so it is as long as the episodes the block will actually
 *    play in one sitting - `episodesPerBlock`, or one - at the series' own average episode.
 */
import { api } from './jellyfin';
import type { ChannelSource } from './types';

export interface Measured {
    minutes: number;
    /** Said in words, for the line under the control. */
    account: string;
    /** Sources the library gave no runtime for; their length is a guess and must be admitted. */
    unknown: string[];
}

const TICKS_PER_MINUTE = 600000000;

interface Timed {
    Id: string;
    Name: string;
    RunTimeTicks?: number;
}

async function minutesOf(source: ChannelSource, episodesPerBlock: number): Promise<{ minutes: number; known: boolean }> {
    if (source.Type === 'Movie') {
        const item = await api().getJSON<Timed>(api().getUrl('Items/' + source.ItemId));
        const minutes = Math.round((item.RunTimeTicks ?? 0) / TICKS_PER_MINUTE);
        return { minutes, known: minutes > 0 };
    }

    const take = source.Type === 'Series' ? Math.max(1, episodesPerBlock || 1) : 200;
    const answer = await api().getItems<{ Items?: Timed[] }>(api().getCurrentUserId(), {
        parentId: source.ItemId,
        includeItemTypes: source.Type === 'Series' ? 'Episode' : undefined,
        recursive: source.Type === 'Series',
        sortBy: 'ParentIndexNumber,IndexNumber',
        sortOrder: 'Ascending',
        limit: take,
        fields: 'RunTimeTicks',
    });

    const items = answer.Items ?? [];
    const timed = items.filter((i) => (i.RunTimeTicks ?? 0) > 0);
    if (timed.length === 0) { return { minutes: 0, known: false }; }

    const total = timed.reduce((sum, i) => sum + (i.RunTimeTicks ?? 0), 0) / TICKS_PER_MINUTE;

    if (source.Type === 'Series') {
        // The average, times how many the block plays - not the whole series, which would make
        // a block months long and is what "the block is as long as its content" cannot mean.
        const average = total / timed.length;
        return { minutes: Math.round(average * Math.max(1, episodesPerBlock || 1)), known: true };
    }

    return { minutes: Math.round(total), known: true };
}

export async function measure(
    sources: ChannelSource[],
    episodesPerBlock: number,
): Promise<Measured> {
    if (sources.length === 0) {
        return { minutes: 0, account: 'This block has no content of its own yet.', unknown: [] };
    }

    const unknown: string[] = [];
    let minutes = 0;

    for (const source of sources) {
        try {
            const one = await minutesOf(source, episodesPerBlock);
            minutes += one.minutes;
            if (!one.known) { unknown.push(source.Name); }
        } catch {
            unknown.push(source.Name);
        }
    }

    const rounded = Math.max(15, Math.round(minutes / 5) * 5);

    const parts: string[] = [];
    const films = sources.filter((s) => s.Type === 'Movie').length;
    const series = sources.filter((s) => s.Type === 'Series').length;
    const collections = sources.filter((s) => s.Type === 'Collection').length;
    if (films) { parts.push(films === 1 ? 'one film' : films + ' films'); }
    if (series) {
        const each = Math.max(1, episodesPerBlock || 1);
        parts.push((series === 1 ? 'one series' : series + ' series')
            + ' at ' + each + (each === 1 ? ' episode' : ' episodes') + ' a sitting');
    }
    if (collections) { parts.push(collections === 1 ? 'one collection' : collections + ' collections'); }

    const account = 'Measured from ' + parts.join(', ')
        + ' — ' + rounded + ' min, rounded to the nearest five.'
        + (unknown.length > 0
            ? ' The library has no length for ' + unknown.join(', ') + ', so that is not counted.'
            : '');

    return { minutes: rounded, account, unknown };
}
