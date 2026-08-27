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
    /**
     * How many weeks the schedule runs for before it repeats. One for every channel that has
     * never been told otherwise; more makes a fortnightly film sayable, which no arrangement
     * of seven days can manage.
     */
    Weeks: number;
    /**
     * Which week of that cycle is on right now, counting from zero.
     *
     * The server's answer, not the page's: which week of a fortnight is running is counted
     * from a fixed Monday, and a page working it out for itself would be a second
     * implementation of the one sum that must never disagree with playback.
     */
    CurrentWeek: number;
    GeneratedUtc: string | null;
    ModifiedUtc: string | null;
    Airings: WeekAiring[];
}

const base = (channelId: string, suffix = '') =>
    api().getUrl('LiteTv/Channels/' + channelId + '/Week' + suffix);

export function getWeek(channelId: string): Promise<Week> {
    return api().getJSON<Week>(base(channelId));
}

/**
 * One change to the week, not yet made.
 *
 * The page holds a list of these rather than sending each one as it happens. That is what lets
 * Save cover the schedule like it covers everything else, and what makes undo the removal of
 * the last element and nothing more.
 */
export type WeekEdit =
    | { Kind: 'Place'; Airing: Partial<WeekAiring> }
    | { Kind: 'Remove'; AiringId: string }
    | { Kind: 'Generate' }
    | { Kind: 'Clear' }
    | { Kind: 'Length'; Weeks: number };

/**
 * Asks what a run of edits comes to, and optionally writes it down.
 *
 * The whole run goes every time. Only the server knows what an appointment does to the rows
 * around it - trims them, cuts one in two, drops one entirely - so a page that tried to draw
 * its own pending edits would be drawing a week nobody is going to get. This way the rehearsal
 * on screen and the week that Save stores are computed from the same input.
 */
export function applyEdits(channelId: string, edits: WeekEdit[], commit: boolean): Promise<Week> {
    return api().fetch<Week>({
        url: base(channelId, '/Edits?commit=' + (commit ? 'true' : 'false')),
        type: 'POST',
        data: JSON.stringify({ Edits: edits }),
        contentType: 'application/json',
        dataType: 'json',
    });
}

/** What an edit is called on screen - for the undo button, and for saying what is waiting. */
export function editWords(edit: WeekEdit): string {
    if (edit.Kind === 'Generate') { return 'laying the week out again'; }
    if (edit.Kind === 'Clear') { return 'emptying the week'; }
    if (edit.Kind === 'Remove') { return 'taking one programme off'; }
    if (edit.Kind === 'Length') {
        return 'making the schedule ' + edit.Weeks + (edit.Weeks === 1 ? ' week long' : ' weeks long');
    }
    return edit.Airing.Id ? 'moving ' + (edit.Airing.Name ?? 'a programme') : 'adding ' + (edit.Airing.Name ?? 'a programme');
}

export const SECONDS_PER_DAY = 24 * 60 * 60;
export const SECONDS_PER_WEEK = 7 * SECONDS_PER_DAY;

/** The longest a schedule may run before it repeats. Matches the server's own cap. */
export const MAX_WEEKS = 13;

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
