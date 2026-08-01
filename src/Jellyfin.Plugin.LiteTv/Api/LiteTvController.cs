using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// API endpoints for LiteTV channels. All endpoints require an authenticated user:
/// the responses expose library content.
/// </summary>
[ApiController]
[Route("LiteTv")]
[Authorize]
public class LiteTvController : ControllerBase
{
    private readonly ChannelPlaylistBuilder _playlistBuilder;
    private readonly TunedSessionMonitor _sessionMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvController"/> class.
    /// </summary>
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="sessionMonitor">The tuned session monitor.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public LiteTvController(
        ChannelPlaylistBuilder playlistBuilder,
        TunedSessionMonitor sessionMonitor,
        ISessionManager sessionManager,
        ILibraryManager libraryManager)
    {
        _playlistBuilder = playlistBuilder;
        _sessionMonitor = sessionMonitor;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the UI options and all enabled channels with what is on air right now.
    /// </summary>
    /// <returns>The guide payload.</returns>
    [HttpGet("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GuideDto> GetChannels()
    {
        var config = Plugin.Instance?.Configuration;
        var result = new GuideDto
        {
            EnableWebUi = config?.EnableWebUi ?? false,
            ShowHomeRow = config?.ShowHomeRow ?? false,
            ShowHeaderButton = config?.ShowHeaderButton ?? false
        };

        foreach (var channel in config?.Channels ?? new())
        {
            if (!channel.Enabled)
            {
                continue;
            }

            var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow, upcomingCount: 1);
            result.Channels.Add(new ChannelSummaryDto
            {
                Id = channel.Id,
                Name = channel.Name,
                Now = now is null ? null : ToProgram(now.Current, now.CurrentStartedUtc),
                Next = now?.Upcoming.Count > 0 ? ToProgram(now.Upcoming[0].Entry, now.Upcoming[0].StartUtc) : null
            });
        }

        return result;
    }

    /// <summary>
    /// Gets the precise on-air position and upcoming programs for one channel.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="upcoming">How many upcoming programs to include.</param>
    /// <returns>The EPG payload, or 404 when the channel is unknown, disabled or empty.</returns>
    [HttpGet("Channels/{channelId}/Now")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ChannelNowDto> GetNow([FromRoute] Guid channelId, [FromQuery] int upcoming = 5)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        if (channel is null)
        {
            return NotFound();
        }

        var now = ScheduleResolver.Resolve(
            _playlistBuilder.GetEntries(channel),
            channel.AnchorUtc,
            DateTime.UtcNow,
            Math.Clamp(upcoming, 0, 20));
        if (now is null)
        {
            return NotFound();
        }

        return new ChannelNowDto
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Current = ToProgram(now.Current, now.CurrentStartedUtc),
            OffsetTicks = now.OffsetTicks,
            ServerTimeUtc = DateTime.UtcNow,
            Upcoming = now.Upcoming.Select(u => ToProgram(u.Entry, u.StartUtc)).ToList()
        };
    }

    /// <summary>
    /// Marks a session as tuned to a channel. The injected web script calls this when it
    /// starts channel playback itself; the server then keeps the account's watch state
    /// clean but does not push follow-up items (the script handles those).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="itemId">The item about to play, to snapshot its user data before playback; optional.</param>
    /// <returns>No content.</returns>
    [HttpPost("Tuned")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Tune([FromQuery] string sessionId, [FromQuery] Guid channelId, [FromQuery] Guid? itemId = null)
    {
        _sessionMonitor.Tune(sessionId, channelId, followSchedule: false, itemId);
        return NoContent();
    }

    /// <summary>
    /// Removes the tuned mark from a session (the viewer left the channel).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Tuned")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Untune([FromQuery] string sessionId)
    {
        _sessionMonitor.Untune(sessionId);
        return NoContent();
    }

    /// <summary>
    /// Tunes another session (e.g. a native TV client) to a channel: sends it a play
    /// command at the live position and lets the server push follow-up items so the
    /// schedule keeps running without the injected script.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="sessionId">The target session id.</param>
    /// <returns>No content, or 404 when the channel is unknown, disabled or empty.</returns>
    [HttpPost("Channels/{channelId}/PlayOn/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PlayOn([FromRoute] Guid channelId, [FromRoute] string sessionId)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        if (channel is null)
        {
            return NotFound();
        }

        var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
        if (now is null)
        {
            return NotFound();
        }

        // The item is registered before the command goes out so its watch state is
        // snapshotted while it is still untouched.
        _sessionMonitor.Tune(sessionId, channelId, followSchedule: true, now.Current.ItemId);
        await _sessionManager.SendPlayCommand(
            sessionId,
            sessionId,
            new PlayRequest
            {
                ItemIds = new[] { now.Current.ItemId },
                StartPositionTicks = now.OffsetTicks,
                PlayCommand = PlayCommand.PlayNow
            },
            HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Suggests channels based on the media present in the library: genre channels,
    /// collection marathons and a kids channel. Used by the configuration page.
    /// </summary>
    /// <returns>The suggestions; already-existing channel names are skipped.</returns>
    [HttpGet("Suggestions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<ChannelSuggestionDto>> GetSuggestions()
    {
        var existingNames = new HashSet<string>(
            Plugin.Instance?.Configuration.Channels.Select(c => c.Name) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<ChannelSuggestionDto>();

        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        }).OfType<Series>().ToList();
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true
        }).OfType<Movie>().Where(m => (m.RunTimeTicks ?? 0) > 0).ToList();

        // Genre channels: the most common genres across series and movies.
        var byGenre = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in series.Cast<BaseItem>().Concat(movies))
        {
            foreach (var genre in item.Genres ?? Array.Empty<string>())
            {
                if (!byGenre.TryGetValue(genre, out var list))
                {
                    byGenre[genre] = list = new List<BaseItem>();
                }

                list.Add(item);
            }
        }

        foreach (var genre in byGenre.Where(g => g.Value.Count >= 4).OrderByDescending(g => g.Value.Count).Take(5))
        {
            var name = genre.Key + "-Kanal";
            if (existingNames.Contains(name))
            {
                continue;
            }

            var picks = genre.Value
                .OrderByDescending(i => i is Series)
                .ThenByDescending(i => i.CommunityRating ?? 0)
                .Take(8);
            suggestions.Add(BuildSuggestion(name, genre.Value.Count + " Titel mit dem Genre \"" + genre.Key + "\"", picks));
        }

        // Marathon channels from collections with enough content.
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true
        }).OfType<BoxSet>();
        foreach (var boxSet in boxSets)
        {
            var children = boxSet.GetLinkedChildren();
            if (children.Count < 3)
            {
                continue;
            }

            var name = "Marathon: " + boxSet.Name;
            if (existingNames.Contains(name))
            {
                continue;
            }

            suggestions.Add(new ChannelSuggestionDto
            {
                Name = name,
                Description = children.Count + " Filme aus der Sammlung \"" + boxSet.Name + "\" in Dauerschleife",
                Sources = new List<SuggestedSourceDto>
                {
                    new() { Type = nameof(ChannelSourceType.Collection), ItemId = boxSet.Id, Name = boxSet.Name ?? string.Empty }
                }
            });
        }

        // Kids channel from FSK-0/FSK-6 rated content.
        var kids = series.Cast<BaseItem>().Concat(movies)
            .Where(i => i.OfficialRating is "FSK-0" or "FSK-6" or "0" or "6")
            .OrderByDescending(i => i is Series)
            .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (kids.Count >= 3 && !existingNames.Contains("Kinderprogramm"))
        {
            suggestions.Add(BuildSuggestion("Kinderprogramm", kids.Count + " Titel mit FSK 0/6", kids.Take(10)));
        }

        return suggestions;
    }

    private static ChannelSuggestionDto BuildSuggestion(string name, string description, IEnumerable<BaseItem> items)
    {
        return new ChannelSuggestionDto
        {
            Name = name,
            Description = description,
            Sources = items.Select(i => new SuggestedSourceDto
            {
                Type = i is Series ? nameof(ChannelSourceType.Series) : nameof(ChannelSourceType.Movie),
                ItemId = i.Id,
                Name = i.Name ?? string.Empty
            }).ToList()
        };
    }

    private static ProgramDto ToProgram(ScheduledEntry entry, DateTime startUtc)
    {
        return new ProgramDto
        {
            ItemId = entry.ItemId,
            Name = entry.Name,
            SeriesName = entry.SeriesName,
            SeriesId = entry.SeriesId,
            StartUtc = startUtc,
            EndUtc = startUtc + TimeSpan.FromTicks(entry.RuntimeTicks),
            RuntimeTicks = entry.RuntimeTicks
        };
    }
}

/// <summary>
/// The channel guide payload.
/// </summary>
public class GuideDto
{
    /// <summary>Gets or sets a value indicating whether the injected web UI is enabled.</summary>
    public bool EnableWebUi { get; set; }

    /// <summary>Gets or sets a value indicating whether the home row is enabled.</summary>
    public bool ShowHomeRow { get; set; }

    /// <summary>Gets or sets a value indicating whether the header guide button is enabled.</summary>
    public bool ShowHeaderButton { get; set; }

    /// <summary>Gets the enabled channels.</summary>
    public List<ChannelSummaryDto> Channels { get; } = new();
}

/// <summary>
/// One channel with its on-air program.
/// </summary>
public class ChannelSummaryDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the program on air right now.</summary>
    public ProgramDto? Now { get; set; }

    /// <summary>Gets or sets the following program.</summary>
    public ProgramDto? Next { get; set; }
}

/// <summary>
/// The precise on-air position for one channel.
/// </summary>
public class ChannelNowDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the program on air.</summary>
    public ProgramDto Current { get; set; } = new();

    /// <summary>Gets or sets how far into the current program the channel is.</summary>
    public long OffsetTicks { get; set; }

    /// <summary>Gets or sets the server time the offset was computed at (UTC), so the
    /// client can correct for its own request latency and clock skew.</summary>
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>Gets or sets the upcoming programs.</summary>
    public List<ProgramDto> Upcoming { get; set; } = new();
}

/// <summary>
/// A suggested channel derived from the library contents.
/// </summary>
public class ChannelSuggestionDto
{
    /// <summary>Gets or sets the suggested channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short human-readable rationale.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the suggested sources.</summary>
    public List<SuggestedSourceDto> Sources { get; set; } = new();
}

/// <summary>
/// One source inside a channel suggestion.
/// </summary>
public class SuggestedSourceDto
{
    /// <summary>Gets or sets the source type name (Movie, Series, Collection).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the library item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item display name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One scheduled program.
/// </summary>
public class ProgramDto
{
    /// <summary>Gets or sets the library item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name for episodes.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the series id for episodes.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the start time (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Gets or sets the end time (UTC).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>Gets or sets the runtime in ticks.</summary>
    public long RuntimeTicks { get; set; }
}
