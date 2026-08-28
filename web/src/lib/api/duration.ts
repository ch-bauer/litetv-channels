/*
 * How long an address actually plays for.
 *
 * There is no seconds box on the Breaks screen and there never will be again: a trailer from
 * YouTube is rarely only the trailer, the television skips the branded card and the plea to
 * subscribe, and the number that matters is what is left. Nobody can type that. The server asks
 * YouTube for the length and SponsorBlock for the skips, and answers with both.
 */
import { api } from '../jellyfin';

export interface SkipSegment {
    StartSeconds: number;
    EndSeconds: number;
    Category: string;
}

export interface Duration {
    VideoId: string | null;
    /**
     * What YouTube calls it, in the language the server asks in. Null when YouTube would not
     * say. Used as an advert's name when nobody typed one - the list used to fall back to the
     * video id, and "aqz-KE-bpKQ" is not a name.
     */
    Title: string | null;
    /** Zero when YouTube would not say - which is not the same as "very short". */
    LengthSeconds: number;
    SkippedSeconds: number;
    /** Zero when the length is unknown, or the skips leave too little to believe. */
    PlayableSeconds: number;
    SkipSegments: SkipSegment[];
}

export function resolveDuration(url: string): Promise<Duration> {
    return api().getJSON<Duration>(api().getUrl('LiteTv/Duration', { url }));
}

export function mmss(seconds: number): string {
    if (seconds <= 0) { return '—'; }
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return m + ':' + String(s).padStart(2, '0');
}

/** What the screen says under the length, in words rather than numbers. */
export function skipNote(duration: Duration): { text: string; good: boolean } {
    if (duration.LengthSeconds <= 0) {
        return { text: 'YouTube would not say how long this is', good: false };
    }
    if (duration.SkippedSeconds <= 0) {
        return { text: 'nothing skipped', good: true };
    }
    return {
        text: mmss(duration.SkippedSeconds) + ' skipped in '
            + duration.SkipSegments.length
            + (duration.SkipSegments.length === 1 ? ' segment' : ' segments'),
        good: true,
    };
}
