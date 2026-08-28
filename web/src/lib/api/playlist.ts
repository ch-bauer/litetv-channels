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
    /** What YouTube calls it. Empty when it would not say; the caller then composes one. */
    Title: string;
    Items: PlaylistItem[];
}

/** True for an address that carries a playlist id, or a bare playlist id. */
export function looksLikePlaylist(value: string): boolean {
    const trimmed = value.trim();
    if (trimmed.length === 0) { return false; }
    if (/[?&]list=/.test(trimmed)) { return true; }
    return !trimmed.includes('/') && !trimmed.includes('?') && /^[A-Za-z0-9_-]{12,}$/.test(trimmed);
}

/**
 * True for anything that is plainly an address rather than a title.
 *
 * The owner asked for links to be detected instead of typed into the box that is for links:
 * pasting one into a search box and being told nothing matches is the page failing to notice
 * what it was handed. Deliberately narrow - a scheme, or a bare host with a dot and a path -
 * so that a film called "Up" is never mistaken for one.
 */
export function looksLikeAddress(value: string): boolean {
    const trimmed = value.trim();
    if (trimmed.length === 0 || /\s/.test(trimmed)) { return false; }
    if (/^https?:\/\//i.test(trimmed)) { return true; }
    return /^[a-z0-9-]+(\.[a-z0-9-]+)+\//i.test(trimmed);
}

export function fetchPlaylist(url: string): Promise<Playlist> {
    return api().getJSON<Playlist>(api().getUrl('LiteTv/YouTubePlaylist', { url }));
}
