using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Publishes the channels to Jellyfin's own Live TV section, so the server's guide - the
/// real one, the grid every client already knows how to draw - is filled with what these
/// channels are actually airing. That is the whole point of being here: the plugin can
/// inject its own guide into the web client, but not into a TV app, and a TV app already
/// has a guide of its own waiting to be told what is on.
/// <para>
/// Nothing is tuned and nothing is transcoded. A channel's stream is the file of the
/// program on air, handed over as it is. The one thing this cannot do is join that program
/// part-way through: Live TV hands a client a stream, not a position in one, so switching
/// on here starts the current program from its beginning. Joining live is what the
/// published "TV-Sender" channel is for, and it is still the better way to watch.
/// </para>
/// </summary>
public class LiteTvLiveService : ILiveTvService
{
    private readonly ChannelGuide _guide;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiteTvLiveService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvLiveService"/> class.
    /// </summary>
    /// <param name="guide">The channel guide.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public LiteTvLiveService(ChannelGuide guide, ILibraryManager libraryManager, ILogger<LiteTvLiveService> logger)
    {
        _guide = guide;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LiteTV";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/ch-bauer/jellyfin-plugin-litetv";

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.PublishAsLiveTv != true)
        {
            return Task.FromResult(Enumerable.Empty<ChannelInfo>());
        }

        var channels = new List<ChannelInfo>();
        var number = 1;
        foreach (var channel in ChannelGuide.Channels())
        {
            channels.Add(new ChannelInfo
            {
                Id = channel.Id.ToString("N"),
                Name = channel.Name,
                Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ChannelType = ChannelType.TV
            });
            number++;
        }

        return Task.FromResult<IEnumerable<ChannelInfo>>(channels);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var channel = ChannelFor(channelId);
        if (channel is null)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var programs = new List<ProgramInfo>();
        foreach (var airing in _guide.Window(channel, startDateUtc, endDateUtc).Take(1024))
        {
            // A dark stretch is not a program. The guide is better off showing a hole than
            // an entry for nothing, which a client would happily offer to record.
            if (airing.Kind == AiringKind.OffAir)
            {
                continue;
            }

            var isInterstitial = airing.Kind == AiringKind.Interstitial;
            programs.Add(new ProgramInfo
            {
                // The start time makes the id: an entry stands for a point in the schedule,
                // and the same program airing again later is a different entry.
                Id = channelId + "_" + airing.StartUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ChannelId = channelId,
                Name = isInterstitial
                    ? "Werbepause" + (airing.NextProgram is null ? string.Empty : " – gleich: " + Title(airing.NextProgram))
                    : Title(airing.Entry!),
                Overview = airing.BlockName,
                StartDate = airing.StartUtc,
                EndDate = airing.EndUtc,
                EpisodeTitle = airing.Entry?.SeriesName is null ? null : airing.Entry.Name,
                IsSeries = airing.Entry?.SeriesName is not null,
                IsMovie = airing.Entry?.SeriesName is null && !isInterstitial,
                IsNews = false,
                IsKids = false,
                IsSports = false
            });
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        return Task.FromResult(CurrentSources(channelId).ToList());
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var source = CurrentSources(channelId).FirstOrDefault();
        if (source is null)
        {
            throw new InvalidOperationException("LiteTV: channel " + channelId + " has nothing on air to stream.");
        }

        return Task.FromResult(source);
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    // ---------------------------------------------------------------- recording
    // There is no tuner and no stream to capture: what a channel airs is a file that is
    // already in the library. Recording it would copy something onto itself, so the timers
    // are answered as empty rather than half-implemented.

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => throw new NotSupportedException("LiteTV airs files that are already in the library; there is nothing to record.");

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => throw new NotSupportedException("LiteTV airs files that are already in the library; there is nothing to record.");

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Title(ScheduledEntry entry)
        => entry.SeriesName is null ? entry.Name : entry.SeriesName + ": " + entry.Name;

    private static Configuration.TvChannel? ChannelFor(string channelId)
        => Guid.TryParse(channelId, out var id) ? ChannelGuide.Channel(id) : null;

    private IEnumerable<MediaSourceInfo> CurrentSources(string channelId)
    {
        var channel = ChannelFor(channelId);
        var airing = channel is null ? null : _guide.NowOn(channel);
        if (airing?.Entry is null)
        {
            _logger.LogWarning("LiteTV: Live TV channel {ChannelId} has nothing on air.", channelId);
            return Array.Empty<MediaSourceInfo>();
        }

        if (_libraryManager.GetItemById(airing.Entry.ItemId) is not MediaBrowser.Controller.Entities.Video video)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        return video.GetMediaSources(true);
    }
}
