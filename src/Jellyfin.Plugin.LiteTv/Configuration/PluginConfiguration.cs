using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiteTv.Configuration;

/// <summary>
/// Plugin configuration: the channel definitions and UI options.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the defined channels.
    /// </summary>
    public List<TvChannel> Channels { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the injected web UI (home row, guide button,
    /// playback overlays) is enabled.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a "TV" row with channel cards is added
    /// to the web client's home screen.
    /// </summary>
    public bool ShowHomeRow { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the "📺" channel-guide button is added
    /// to the web client's header. Independent of the home row and overlays, so the
    /// button can be hidden while those stay on.
    /// </summary>
    public bool ShowHeaderButton { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the channels are published as a Jellyfin
    /// channel, which is what makes them browsable and playable on clients the plugin
    /// cannot inject its web UI into - TV apps, phones, anything but the web client.
    /// </summary>
    public bool PublishAsChannels { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the channels are also published to
    /// Jellyfin's own Live TV section, which fills the server's real guide - the grid every
    /// client already knows how to draw - with what the channels are airing. Switching a
    /// channel on there starts the current program from its beginning rather than joining
    /// it live, because Live TV hands a client a stream and not a position in one.
    /// </summary>
    public bool PublishAsLiveTv { get; set; }
}

/// <summary>
/// A virtual TV channel: an ordered, endlessly looping queue of library content.
/// The schedule is fully deterministic: what is on "now" derives from the wall clock,
/// the anchor timestamp and the item runtimes. (v2 may add optional time blocks.)
/// </summary>
public class TvChannel
{
    /// <summary>
    /// Gets or sets the channel id.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the channel display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the channel is on air.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the schedule zero point (UTC). The loop position is
    /// (now - anchor) modulo the total queue runtime.
    /// </summary>
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the ordered content sources making up the loop.
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets how many consecutive items are taken from each source before
    /// rotating to the next one, interleaving the channel's sources. For example, two
    /// series with a block of 2 play as S1E1, S1E2, S2E1, S2E2, S1E3, S1E4, ... The
    /// default of 0 keeps the classic behaviour: each source is played in full before
    /// the next (a marathon).
    /// </summary>
    public int EpisodesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the order the queue is played in.
    /// </summary>
    public PlayOrder Order { get; set; }

    /// <summary>
    /// Gets or sets the grid programs start on, in minutes. Zero - the default - runs
    /// them back to back, the way the channel always has. With 30, a program starts only
    /// on the hour or the half hour and the rest of its slot is time to fill; that is what
    /// makes "the film at 20:15" a thing the channel can actually promise.
    /// </summary>
    public int SlotMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the time a slot leaves over is filled with
    /// trailers for the program about to start, rather than left as dead air.
    /// </summary>
    public bool TrailersInGaps { get; set; } = true;

    /// <summary>
    /// Gets or sets the program blocks: parts of the week that air something other than
    /// the channel's own <see cref="Sources"/>. Whatever no block covers is aired from
    /// <see cref="Sources"/>, so a channel with no blocks behaves exactly as it always
    /// has, and a channel whose blocks leave gaps falls back to its own lineup.
    /// </summary>
    public List<ProgramBlock> Blocks { get; set; } = new();
}

/// <summary>
/// A part of the week that airs its own lineup: the kids' programming until noon, the
/// film on Saturday evening. A block owns a window of the day on the weekdays it applies
/// to, and holds its own sources, so the channel's identity can change with the hour
/// without needing a second channel.
/// </summary>
public class ProgramBlock
{
    /// <summary>
    /// Gets or sets the block name, shown in the guide.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the block is in use.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets when the block starts, in minutes after local midnight.
    /// </summary>
    public int StartMinutes { get; set; }

    /// <summary>
    /// Gets or sets how long the block runs, in minutes. A block may run past midnight;
    /// it then continues into the following day.
    /// </summary>
    public int DurationMinutes { get; set; } = 240;

    /// <summary>
    /// Gets or sets the local weekdays the block starts on. Empty means every day.
    /// </summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>
    /// Gets or sets the block's own content.
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the block's source interleaving, as on the channel.
    /// </summary>
    public int EpisodesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the order the block's queue is played in.
    /// </summary>
    public PlayOrder Order { get; set; }
}

/// <summary>
/// The order a queue is played in.
/// </summary>
public enum PlayOrder
{
    /// <summary>Source order: each source in full, or interleaved in blocks.</summary>
    Sequential,

    /// <summary>
    /// Shuffled - but shuffled once and for good, not re-drawn on every request. A
    /// schedule that reshuffles is not a schedule: the guide would promise one thing and
    /// air another, and a viewer already watching would be moved mid-program.
    /// </summary>
    Shuffle
}

/// <summary>
/// One entry in a channel's queue: a movie, a series (expanded to all episodes in
/// chronological order) or a collection (expanded to its children by premiere date).
/// </summary>
public class ChannelSource
{
    /// <summary>
    /// Gets or sets the source type.
    /// </summary>
    public ChannelSourceType Type { get; set; }

    /// <summary>
    /// Gets or sets the library item id (movie, series or collection).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the display name at the time the source was added
    /// (config-page convenience only; the library remains authoritative).
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The kind of library item a <see cref="ChannelSource"/> references.
/// </summary>
public enum ChannelSourceType
{
    /// <summary>A single movie.</summary>
    Movie,

    /// <summary>A TV series, expanded to its episodes in aired order.</summary>
    Series,

    /// <summary>A collection (box set), expanded to its children.</summary>
    Collection
}
