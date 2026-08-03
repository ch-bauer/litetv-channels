using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Publishes the configured LiteTV channels as a Jellyfin channel, so they can be browsed
/// and played on every client rather than only in the web UI the plugin injects into.
/// Each configured channel becomes a folder holding one item: whatever is on air. That item
/// keeps a stable id so it stays the same entry as the schedule moves on, and its media is
/// resolved when playback starts (<see cref="GetChannelItemMediaInfo"/>), which is what makes
/// it play the current program's own file directly - no stream is generated, nothing is
/// transcoded on the plugin's account.
/// </summary>
public class LiteTvChannelProvider : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelGuide _guide;
    private readonly ILogger<LiteTvChannelProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvChannelProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="guide">The channel guide.</param>
    /// <param name="logger">The logger.</param>
    public LiteTvChannelProvider(
        ILibraryManager libraryManager,
        ChannelGuide guide,
        ILogger<LiteTvChannelProvider> logger)
    {
        _libraryManager = libraryManager;
        _guide = guide;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "TV-Sender";

    /// <inheritdoc />
    public string Description => "Durchgehende TV-Sender aus der eigenen Bibliothek.";

    /// <summary>
    /// Gets the data version. Bumping it makes the server discard what it cached for this
    /// channel, so it changes with the channel configuration rather than with the clock -
    /// what is on air moves constantly and is handled by the cache key instead.
    /// </summary>
    public string DataVersion => ConfigurationVersion();

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/ch-bauer/jellyfin-plugin-litetv";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video }
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        var config = Plugin.Instance?.Configuration;
        return config is not null && config.PublishAsChannels && config.Channels.Any(c => c.Enabled);
    }

    /// <summary>
    /// Gets a key that changes whenever a different program is on air, which is how the
    /// server is told that its cached listing is stale. Without it the folder would keep
    /// showing the program that was on when it was last queried.
    /// </summary>
    /// <param name="userId">The user the listing is for.</param>
    /// <returns>The cache key.</returns>
    public string GetCacheKey(string? userId)
    {
        var parts = new List<string>();
        foreach (var channel in EnabledChannels())
        {
            var (now, _) = Resolve(channel, 0);
            parts.Add(now?.Entry is null ? channel.Id + ":-" : channel.Id + ":" + now.Entry.ItemId);
        }

        return string.Join('|', parts);
    }

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // One entry per channel, and nothing below it. A channel is a single thing to
        // switch on; listing its programs as entries of their own turned it into a pile of
        // films to start individually, which is not what a channel is. What is coming up
        // belongs in the entry's description, where it can be read but not played.
        var items = new List<ChannelItemInfo>();
        foreach (var channel in EnabledChannels())
        {
            var entry = ChannelEntry(channel);
            if (entry is not null)
            {
                items.Add(entry);
            }
        }

        return Task.FromResult(new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        });
    }

    /// <summary>
    /// Resolves the media of the program currently on air, at playback time. The item's id
    /// names only the channel, so whatever is on when the viewer presses play is what runs.
    /// </summary>
    /// <param name="id">The channel item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current program's media sources.</returns>
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        // The program is taken from the id rather than from the clock: the server caches
        // what this returns for the rest of its run, so the answer has to stay true.
        var programId = ProgramIdFromItemId(id);
        if (programId is null)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        if (_libraryManager.GetItemById(programId.Value) is not MediaBrowser.Controller.Entities.Video video)
        {
            _logger.LogWarning("LiteTV: the program behind channel item {Id} is no longer in the library.", id);
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        return Task.FromResult(video.GetMediaSources(true).AsEnumerable());
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    /// <summary>
    /// Builds the id of a channel's on-air item, naming both the channel and the program.
    /// The program has to be part of it: the server resolves an item's media once and then
    /// keeps that answer for the rest of its run, so an id that outlived the program would
    /// keep playing the program it was first resolved for.
    /// </summary>
    /// <param name="channelId">The LiteTV channel id.</param>
    /// <param name="programId">The library item on air.</param>
    /// <returns>The channel item id.</returns>
    internal static string NowPlayingId(Guid channelId, Guid programId)
        => "now_" + channelId.ToString("N") + "_" + programId.ToString("N");

    /// <summary>
    /// Reads the channel back out of an on-air item id.
    /// </summary>
    /// <param name="channelItemId">The channel item id.</param>
    /// <returns>The LiteTV channel id, or null when the id is not one of ours.</returns>
    internal static Guid? ChannelIdFromItemId(string channelItemId) => IdPart(channelItemId, 0);

    /// <summary>
    /// Reads the program back out of an on-air item id.
    /// </summary>
    /// <param name="channelItemId">The channel item id.</param>
    /// <returns>The library item the entry was created for, or null when the id is not ours.</returns>
    internal static Guid? ProgramIdFromItemId(string channelItemId) => IdPart(channelItemId, 1);

    private static Guid? IdPart(string channelItemId, int index)
    {
        if (!channelItemId.StartsWith("now_", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = channelItemId[4..].Split('_');
        return parts.Length > index && Guid.TryParse(parts[index], out var id) ? id : null;
    }

    private static IEnumerable<TvChannel> EnabledChannels()
    {
        return Plugin.Instance?.Configuration.Channels.Where(c => c.Enabled) ?? Enumerable.Empty<TvChannel>();
    }

    private static string ConfigurationVersion()
    {
        var channels = EnabledChannels()
            .Select(c => c.Id.ToString("N") + c.Name + c.Sources.Count + c.EpisodesPerBlock);
        return string.Join('|', channels).GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How many programs a channel folder lists beyond the one on air, so it reads as a
    /// schedule rather than a single entry.
    /// </summary>
    private const int LineupLength = 9;

    /// <summary>
    /// Gets what the channel is airing and what follows, as the entry needs both: one to
    /// play and the other to describe.
    /// </summary>
    private (Airing? Now, IReadOnlyList<Airing> Upcoming) Resolve(TvChannel channel, int upcomingCount = 1)
    {
        var at = DateTime.UtcNow;
        var window = _guide.Window(channel, at, at.AddHours(12)).Take(256).ToList();
        var now = window.FirstOrDefault();
        var upcoming = window.Skip(1)
            .Where(a => a.Kind == AiringKind.Program)
            .Take(Math.Max(0, upcomingCount))
            .ToList();

        return (now, upcoming);
    }

    /// <summary>
    /// The one entry a channel has: switch it on and it joins whatever is on air. What
    /// follows is written into the description, in order, so the schedule can be read
    /// without turning each program into something to start on its own.
    /// The entry stands for one program even so, because the server resolves an item's
    /// media once and keeps that answer - so it is rebuilt as the schedule moves on, which
    /// is also what keeps the description current.
    /// </summary>
    private ChannelItemInfo? ChannelEntry(TvChannel channel)
    {
        var (now, upcoming) = Resolve(channel, LineupLength);
        if (now?.Entry is null)
        {
            return null;
        }

        var current = now.Entry;

        // Carry the media on the entry itself, not only through the callback. A client
        // reads container and stream details off the item it is about to play, and an entry
        // that says nothing about its media is one some players will not start.
        var media = _libraryManager.GetItemById(current.ItemId) as MediaBrowser.Controller.Entities.Video;
        var sources = media?.GetMediaSources(true).ToList() ?? new List<MediaSourceInfo>();

        var entry = new ChannelItemInfo
        {
            Id = NowPlayingId(channel.Id, current.ItemId),
            MediaSources = sources,
            Name = channel.Name,
            Overview = Schedule(now, upcoming),
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            RunTimeTicks = current.RuntimeTicks,
            StartDate = now.StartUtc,
            EndDate = now.EndUtc,
            IsLiveStream = false
        };

        // The entry wears the artwork of what it is airing. Without it the channel is a blank
        // rectangle wherever it is listed, and a client that draws its pause screen from the
        // item it is playing has nothing but the schedule text to draw. The path, not a URL:
        // the file is on this server, so no round trip and no token to authenticate it.
        var artwork = ArtworkPath(media, current.SeriesId);
        if (!string.IsNullOrEmpty(artwork))
        {
            entry.ImageUrl = artwork;
        }

        return entry;
    }

    /// <summary>
    /// Gets the poster a programme is recognised by, falling back to its series where an
    /// episode has no image of its own.
    /// </summary>
    private string? ArtworkPath(MediaBrowser.Controller.Entities.BaseItem? item, Guid? seriesId)
    {
        if (item?.PrimaryImagePath is { Length: > 0 } own)
        {
            return own;
        }

        if (seriesId is { } id && id != Guid.Empty)
        {
            return _libraryManager.GetItemById(id)?.PrimaryImagePath;
        }

        return null;
    }

    /// <summary>
    /// Writes out what is on and what follows, as the entry's description.
    /// </summary>
    private static string Schedule(Airing now, IReadOnlyList<Airing> upcoming)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(now.BlockName))
        {
            lines.Add(now.BlockName);
            lines.Add(string.Empty);
        }

        lines.Add(now.Kind == AiringKind.Interstitial
            ? "Werbepause bis " + Clock(now.EndUtc)
            : "Jetzt: " + Title(now.Entry!));
        lines.Add(string.Empty);
        lines.Add("Danach:");

        foreach (var airing in upcoming)
        {
            lines.Add(Clock(airing.StartUtc) + "  " + Title(airing.Entry!));
        }

        return string.Join('\n', lines);
    }

    private static string Title(ScheduledEntry entry)
        => entry.SeriesName is null ? entry.Name : entry.SeriesName + ": " + entry.Name;

    private static string Clock(DateTime utc)
        => utc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
