/*
 * The plugin's configuration, as it crosses the wire.
 *
 * Mirrors Configuration/PluginConfiguration.cs. Two things measured rather than assumed:
 * enums arrive as **strings** ('Sequential', 'Series'), and Jellyfin writes GUIDs **without
 * dashes** in some places and with them in others - so every id is handled as an opaque string
 * and never parsed here.
 */

export type PlayOrder = 'Sequential' | 'Shuffle';

/**
 * How trailers are worked into the queue. Mirrors the server's `TrailerMode` enum, and it is
 * spelled out as a union rather than `string` for one reason: a new channel was created with
 * `'Between'`, which is not a member, and the server answers a **500** to the whole
 * configuration - so no channel could be saved, not just the new one. A wrong value here is
 * unsaveable, and that is worth a compile error.
 */
export type TrailerMode = 'Off' | 'Preview' | 'Manual' | 'Both';
export type ChannelSourceType = 'Movie' | 'Series' | 'Episode' | 'Collection' | 'YouTube';

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
    /** Start the next selected item on each weekly block occurrence. */
    AdvanceOnePerWeek: boolean;
    TrailerEnabled: boolean;
    TrailerProgramsBefore: number;
}

export interface TvChannel {
    Id: string;
    /**
     * Where the channel sits in the list, counting from one.
     *
     * A folder of one file per channel has no order of its own, so the server writes the
     * position down and hands a new channel the end of the list. Nothing on the page sets it;
     * it is carried so a save round-trips what the server said.
     */
    Position: number;
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
    Trailers: TrailerMode;
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
    /**
     * The playback account's current access token.
     *
     * The page never reads or writes this - it is declared so it is carried back untouched on
     * save. Blanking it would make the next tune-in authenticate afresh, and because Jellyfin
     * keeps one session per device id, authenticating revokes the token whatever is playing is
     * using. In other words: drop this field and pressing Save stops the television.
     */
    ChannelUserToken: string;
    SkipTrailerSegments: boolean;
    YouTubeClient: string;
    /** The language the configuration page is written in: `auto`, `en` or `de`. */
    PageLanguage: string;
    /**
     * The language YouTube is asked to answer in - a tag like `de` or `de-DE`. Empty follows
     * `PageLanguage`, then the server's own culture. It is what a YouTube programme is CALLED
     * in the schedule.
     */
    YouTubeLanguage: string;
    ProofOfOriginToken: string;
    ProofOfOriginVisitorData: string;
    ProofOfOriginMintedUtc: string | null;
}
