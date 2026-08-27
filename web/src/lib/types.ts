/*
 * The plugin's configuration, as it crosses the wire.
 *
 * Mirrors Configuration/PluginConfiguration.cs. Two things measured rather than assumed:
 * enums arrive as **strings** ('Sequential', 'Series'), and Jellyfin writes GUIDs **without
 * dashes** in some places and with them in others - so every id is handled as an opaque string
 * and never parsed here.
 */

export type PlayOrder = 'Sequential' | 'Shuffle';
export type ChannelSourceType = 'Movie' | 'Series' | 'Collection' | 'YouTube';

export interface ChannelSource {
    Type: ChannelSourceType;
    ItemId: string;
    Name: string;
    /** Set only for a YouTube source, which has no library item behind it at all. */
    Url?: string;
}

export interface ProgramBlock {
    Name: string;
    Enabled: boolean;
    StartMinutes: number;
    DurationMinutes: number;
    Days: string[];
    Sources: ChannelSource[];
    EpisodesPerBlock: number;
    Order: PlayOrder;
}

export interface TvChannel {
    Id: string;
    Name: string;
    Enabled: boolean;
    AnchorUtc: string;
    Sources: ChannelSource[];
    Adverts: unknown[];
    ScheduleEdits: unknown[];
    EpisodesPerBlock: number;
    Order: PlayOrder;
    SlotMinutes: number;
    TrailersInGaps: boolean;
    Trailers: string;
    TrailerEveryPrograms: number;
    TrailerLookahead: number;
    TrailerTitles: ChannelSource[];
    Blocks: ProgramBlock[];
    TrailerSlots: unknown[];
    Artwork: Record<string, unknown>;
}

export interface PluginConfig {
    Channels: TvChannel[];
    ChannelUserName: string;
    ChannelUserPassword: string;
    SkipTrailerSegments: boolean;
    YouTubeClient: string;
    ProofOfOriginToken: string;
    ProofOfOriginVisitorData: string;
    ProofOfOriginMintedUtc: string | null;
}
