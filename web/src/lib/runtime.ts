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

/** The one library item a weekly film block has selected for this occurrence. */
export async function measureWeeklySelection(
    sources: ChannelSource[],
    occurrence: number,
): Promise<Measured> {
    const items: Timed[] = [];
    for (const source of sources) {
        if (isEmptyId(source.ItemId)) { continue; }
        if (source.Type === 'Movie') {
            items.push(await api().getJSON<Timed>(api().getUrl('Items/' + source.ItemId)));
            continue;
        }

        if (source.Type === 'Collection') {
            const collection = await api().getJSON<{ LinkedChildren?: Array<{ ItemId?: string }> }>(
                api().getUrl('Items/' + source.ItemId, { fields: 'LinkedChildren' }),
            );
            const children = (collection.LinkedChildren ?? []).filter((child) => Boolean(child.ItemId));
            items.push(...await Promise.all(children.map((child) =>
                api().getJSON<Timed>(api().getUrl('Items/' + child.ItemId)),
            )));
            continue;
        }

        const answer = await api().getItems<{ Items?: Timed[] }>(api().getCurrentUserId(), {
            parentId: source.ItemId,
            includeItemTypes: source.Type === 'Series' ? 'Episode' : undefined,
            recursive: source.Type === 'Series',
            sortBy: 'ParentIndexNumber,IndexNumber',
            sortOrder: 'Ascending',
            limit: 500,
            fields: 'RunTimeTicks',
        });
        items.push(...(answer.Items ?? []));
    }

    const selected = items.filter((item) => (item.RunTimeTicks ?? 0) > 0);
    if (selected.length === 0) {
        return { minutes: 0, account: 'This block has no measurable film for this week.', unknown: [] };
    }

    const item = selected[((occurrence % selected.length) + selected.length) % selected.length];
    const minutes = Math.max(1, Math.round((item.RunTimeTicks ?? 0) / TICKS_PER_MINUTE));
    return { minutes, account: 'This week: ' + item.Name + ' — ' + minutes + ' min.', unknown: [] };
}

/** Whether an id names nothing - dash-less or not; see the note in deal.ts. */
function isEmptyId(id: string | null | undefined): boolean {
    return !id || id.replace(/-/g, '').replace(/0/g, '').length === 0;
}

async function minutesOf(source: ChannelSource, episodesPerBlock: number): Promise<{ minutes: number; known: boolean }> {
    // Nothing of the library's behind it - a YouTube playlist. Asking about the all-zero id is
    // a 400 from Jellyfin, not an empty answer: see the note in deal.ts.
    if (isEmptyId(source.ItemId)) {
        return { minutes: 0, known: false };
    }

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
