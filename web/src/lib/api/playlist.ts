/*
 * What is in a YouTube playlist.
 *
 * The plugin could always expand a playlist when a week was laid out, but nothing could ask - so
 * adding one on the Content screen put a row called "YouTube playlist" on the list and gave no
 * sign whether the address was any good. This is the asking half.
 *
 * Nothing here is stored. The week is still expanded from the address when it is laid out, so a
 * playlist that gains a video reaches the channel at the next lay-out rather than the moment it
 * changes underneath a written-down schedule.
 */
import { api } from '../jellyfin';

export interface PlaylistItem {
    VideoId: string;
    Title: string;
    Url: string;
    /** Zero when YouTube's page did not say. */
    Seconds: number;
}

export interface Playlist {
    PlaylistId: string;
    Items: PlaylistItem[];
}

/** True for an address that carries a playlist id, or a bare playlist id. */
export function looksLikePlaylist(value: string): boolean {
    const trimmed = value.trim();
    if (trimmed.length === 0) { return false; }
    if (/[?&]list=/.test(trimmed)) { return true; }
    return !trimmed.includes('/') && !trimmed.includes('?') && /^[A-Za-z0-9_-]{12,}$/.test(trimmed);
}

export function fetchPlaylist(url: string): Promise<Playlist> {
    return api().getJSON<Playlist>(api().getUrl('LiteTv/YouTubePlaylist', { url }));
}
