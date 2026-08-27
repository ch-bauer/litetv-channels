/*
 * The stored week.
 *
 * A week is the server's, not the page's: it is generated there, edited there, and every call
 * here answers with the whole week back, so the page never has to guess what an edit did. That
 * is deliberate - the schedule bends around an edit (an edit is an appointment), and only the
 * server knows how.
 */
import { api } from '../jellyfin';

export type AiringKind = 'Programme' | 'Trailer' | 'Advert' | 'Gap';

export interface WeekAiring {
    /** Null for a hole in the week: nothing is stored, so there is nothing to address. */
    Id: string | null;
    /** Seconds after Monday 00:00, local. */
    StartSecond: number;
    DurationSeconds: number;
    Kind: AiringKind;
    ItemId: string | null;
    Name: string;
    Url: string;
    OffsetTicks: number;
    SeriesName: string | null;
    BlockName: string | null;
    TrailedItemId: string | null;
    TrailedName: string | null;
    /** A hole long enough to count as off air rather than between programmes. */
    OffAir: boolean;
}

export interface Week {
    ChannelId: string;
    ChannelName: string;
    /** False means nobody has laid a week out; the channel airs from its sources instead. */
    Curated: boolean;
    GeneratedUtc: string | null;
    ModifiedUtc: string | null;
    Airings: WeekAiring[];
}

const base = (channelId: string, suffix = '') =>
    api().getUrl('LiteTv/Channels/' + channelId + '/Week' + suffix);

export function getWeek(channelId: string): Promise<Week> {
    return api().getJSON<Week>(base(channelId));
}

export function generateWeek(channelId: string): Promise<Week> {
    return api().fetch<Week>({ url: base(channelId, '/Generate'), type: 'POST', dataType: 'json' });
}

/** Adds or moves one airing. The server answers with the week as it now stands. */
export function putAiring(channelId: string, airing: Partial<WeekAiring>): Promise<Week> {
    return api().fetch<Week>({
        url: base(channelId, '/Airings'),
        type: 'PUT',
        data: JSON.stringify(airing),
        contentType: 'application/json',
        dataType: 'json',
    });
}

export function deleteAiring(channelId: string, airingId: string): Promise<Week> {
    return api().fetch<Week>({
        url: base(channelId, '/Airings/' + airingId),
        type: 'DELETE',
        dataType: 'json',
    });
}

export function clearWeek(channelId: string): Promise<Week> {
    return api().fetch<Week>({ url: base(channelId), type: 'DELETE', dataType: 'json' });
}

export const SECONDS_PER_DAY = 24 * 60 * 60;

/** Which day of the stored week a second falls in, 0 = Monday. */
export function dayOf(second: number): number {
    return Math.floor(second / SECONDS_PER_DAY);
}

export function secondOfDay(second: number): number {
    return second % SECONDS_PER_DAY;
}

/** Where "now" sits in the stored week, in seconds after Monday 00:00 local. */
export function nowSecond(at = new Date()): number {
    // JavaScript counts Sunday as 0; the stored week starts on Monday.
    const day = (at.getDay() + 6) % 7;
    return day * SECONDS_PER_DAY + at.getHours() * 3600 + at.getMinutes() * 60 + at.getSeconds();
}

export function clock(secondOfTheDay: number): string {
    const h = Math.floor(secondOfTheDay / 3600) % 24;
    const m = Math.floor(secondOfTheDay / 60) % 60;
    return String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
}

export const DAY_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
export const DAY_SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

export const KIND_FILL: Record<AiringKind, string> = {
    Programme: '#5b6ee1',
    Trailer: '#d99a3a',
    Advert: '#2f9e8f',
    Gap: 'transparent',
};
