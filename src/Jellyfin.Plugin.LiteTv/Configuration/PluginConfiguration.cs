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
