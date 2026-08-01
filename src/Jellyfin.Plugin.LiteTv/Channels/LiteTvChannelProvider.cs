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
    private readonly ChannelPlaylistBuilder _playlistBuilder;
    private readonly ILogger<LiteTvChannelProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvChannelProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="logger">The logger.</param>
    public LiteTvChannelProvider(
        ILibraryManager libraryManager,
        ChannelPlaylistBuilder playlistBuilder,
        ILogger<LiteTvChannelProvider> logger)
    {
        _libraryManager = libraryManager;
        _playlistBuilder = playlistBuilder;
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
            var now = Resolve(channel);
            parts.Add(now is null ? channel.Id + ":-" : channel.Id + ":" + now.Current.ItemId);
        }

        return string.Join('|', parts);
    }

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = string.IsNullOrEmpty(query.FolderId)
            ? EnabledChannels().Select(ChannelFolder).ToList()
            : NowPlayingItems(query.FolderId);

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

    private static TvChannel? ChannelFor(string channelItemId)
    {
        var raw = channelItemId.StartsWith("now_", StringComparison.Ordinal) ? channelItemId[4..] : channelItemId;
        return Guid.TryParse(raw, out var id)
            ? EnabledChannels().FirstOrDefault(c => c.Id == id)
            : null;
    }

    private ScheduleNow? Resolve(TvChannel channel)
    {
        return ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow, upcomingCount: 1);
    }

    private static ChannelItemInfo ChannelFolder(TvChannel channel)
    {
        return new ChannelItemInfo
        {
            Id = channel.Id.ToString("N"),
            Name = channel.Name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container
        };
    }

    private List<ChannelItemInfo> NowPlayingItems(string folderId)
    {
        var channel = ChannelFor(folderId);
        var now = channel is null ? null : Resolve(channel);
        if (channel is null || now is null)
        {
            return new List<ChannelItemInfo>();
        }

        var current = now.Current;
        var title = current.SeriesName is null ? current.Name : current.SeriesName + ": " + current.Name;
        var next = now.Upcoming.Count > 0 ? now.Upcoming[0].Entry : null;
        var overview = next is null
            ? "Jetzt: " + title
            : "Jetzt: " + title + "\nDanach: " + (next.SeriesName is null ? next.Name : next.SeriesName + ": " + next.Name);

        // Carry the media on the entry itself, not only through the callback. A client
        // reads container and stream details off the item it is about to play, and an entry
        // that says nothing about its media is one some players will not start.
        var media = _libraryManager.GetItemById(current.ItemId) as MediaBrowser.Controller.Entities.Video;
        var sources = media?.GetMediaSources(true).ToList() ?? new List<MediaSourceInfo>();

        return new List<ChannelItemInfo>
        {
            new()
            {
                Id = NowPlayingId(channel.Id, current.ItemId),
                MediaSources = sources,
                Name = channel.Name + " - " + title,
                Overview = overview,
                Type = ChannelItemType.Media,
                ContentType = ChannelMediaContentType.Movie,
                MediaType = ChannelMediaType.Video,
                RunTimeTicks = current.RuntimeTicks,
                // The media itself is resolved when playback starts, so the entry keeps
                // pointing at whatever is on air rather than at the program listed here.
                IsLiveStream = false
            }
        };
    }
}
