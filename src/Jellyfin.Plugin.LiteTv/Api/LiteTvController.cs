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
    /// <summary>How far ahead the guide grid looks when no window is asked for.</summary>
    private const int DefaultGuideHours = 4;

    private readonly ChannelGuide _guide;
    private readonly TunedSessionMonitor _sessionMonitor;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvController"/> class.
    /// </summary>
    /// <param name="guide">The channel guide.</param>
    /// <param name="sessionMonitor">The tuned session monitor.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public LiteTvController(
        ChannelGuide guide,
        TunedSessionMonitor sessionMonitor,
        ISessionManager sessionManager,
        ILibraryManager libraryManager)
    {
        _guide = guide;
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

        foreach (var channel in ChannelGuide.Channels())
        {
            var window = _guide.Window(channel, DateTime.UtcNow, DateTime.UtcNow.AddHours(DefaultGuideHours)).Take(24).ToList();
            var now = window.FirstOrDefault();
            var nextProgram = window.Skip(1).FirstOrDefault(a => a.Kind == AiringKind.Program);

            result.Channels.Add(new ChannelSummaryDto
            {
                Id = channel.Id,
                Name = channel.Name,
                Kind = now?.Kind.ToString() ?? nameof(AiringKind.OffAir),
                BlockName = string.IsNullOrEmpty(now?.BlockName) ? null : now!.BlockName,
                Now = now is null || now.Entry is null ? null : ToProgram(now),
                Next = nextProgram is null ? null : ToProgram(nextProgram)
            });
        }

        return result;
    }

    /// <summary>
    /// Gets every channel's programming over a window of time: the grid a guide is drawn
    /// from. This is the whole schedule, interstitials and dark stretches included, not
    /// just the programs - a guide that silently skipped them would not add up.
    /// </summary>
    /// <param name="from">Where the window starts (UTC); defaults to now.</param>
    /// <param name="hours">How many hours it covers.</param>
    /// <returns>The guide grid.</returns>
    [HttpGet("Guide")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GuideWindowDto> GetGuide([FromQuery] DateTime? from = null, [FromQuery] double hours = DefaultGuideHours)
    {
        var start = (from ?? DateTime.UtcNow).ToUniversalTime();
        var end = start.AddHours(Math.Clamp(hours, 0.5, 48));
        var result = new GuideWindowDto { StartUtc = start, EndUtc = end, ServerTimeUtc = DateTime.UtcNow };

        foreach (var channel in ChannelGuide.Channels())
        {
            var row = new GuideChannelDto { Id = channel.Id, Name = channel.Name };
            foreach (var airing in _guide.Window(channel, start, end).Take(512))
            {
                row.Programs.Add(ToProgram(airing));
            }

            result.Channels.Add(row);
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
        var channel = ChannelGuide.Channel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var at = DateTime.UtcNow;
        var window = _guide.Window(channel, at, at.AddHours(12)).Take(256).ToList();
        var current = window.FirstOrDefault();
        if (current is null)
        {
            return NotFound();
        }

        var following = window.Skip(1).Where(a => a.Kind == AiringKind.Program).ToList();
        var next = following.FirstOrDefault();

        return new ChannelNowDto
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Kind = current.Kind.ToString(),
            BlockName = string.IsNullOrEmpty(current.BlockName) ? null : current.BlockName,
            Current = current.Entry is null ? null : ToProgram(current),
            OffsetTicks = current.OffsetAt(at),
            EndUtc = current.EndUtc,
            NextProgram = next is null ? null : ToProgram(next),
            // What the web client can play over an interstitial the library only knows the
            // address of - the usual case, since trailers are far more often linked than held.
            Trailers = next is null ? new List<TrailerDto>() : RemoteTrailers(next.Entry!.ItemId),
            ServerTimeUtc = at,
            Upcoming = following.Take(Math.Clamp(upcoming, 0, 20)).Select(ToProgram).ToList()
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
        var channel = ChannelGuide.Channel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var at = DateTime.UtcNow;
        var airing = _guide.NowOn(channel, at);
        if (airing?.Entry is null)
        {
            return NotFound();
        }

        // The item is registered before the command goes out so its watch state is
        // snapshotted while it is still untouched.
        _sessionMonitor.Tune(sessionId, channelId, followSchedule: true, airing.Entry.ItemId);
        await _sessionManager.SendPlayCommand(
            sessionId,
            sessionId,
            new PlayRequest
            {
                ItemIds = new[] { airing.Entry.ItemId },
                StartPositionTicks = airing.OffsetAt(at),
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

    private static ProgramDto ToProgram(Airing airing)
    {
        var entry = airing.Entry;
        return new ProgramDto
        {
            ItemId = entry?.ItemId ?? Guid.Empty,
            Name = entry?.Name ?? (airing.Kind == AiringKind.OffAir ? "Sendepause" : "Werbepause"),
            SeriesName = entry?.SeriesName,
            SeriesId = entry?.SeriesId,
            StartUtc = airing.StartUtc,
            EndUtc = airing.EndUtc,
            RuntimeTicks = entry?.RuntimeTicks ?? (airing.EndUtc - airing.StartUtc).Ticks,
            Kind = airing.Kind.ToString(),
            BlockName = string.IsNullOrEmpty(airing.BlockName) ? null : airing.BlockName,
            // A program the block boundary cut into: it does not start at its beginning.
            StartOffsetTicks = airing.OffsetTicks,
            NextProgramName = airing.NextProgram?.Name
        };
    }

    /// <summary>
    /// Gets the trailers the library only holds an address for - almost always YouTube,
    /// which is what the metadata providers fill in. Nothing the server can schedule, but
    /// the web client can embed them, and that is what fills an interstitial in practice.
    /// </summary>
    private List<TrailerDto> RemoteTrailers(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item?.RemoteTrailers
            .Where(t => !string.IsNullOrEmpty(t.Url))
            .Select(t => new TrailerDto { Name = t.Name ?? string.Empty, Url = t.Url })
            .Take(4)
            .ToList() ?? new List<TrailerDto>();
    }
}

/// <summary>
/// A trailer the library links to rather than holds.
/// </summary>
public class TrailerDto
{
    /// <summary>Gets or sets the trailer name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the address it plays from.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Every channel's programming over a window of time: the grid a guide is drawn from.
/// </summary>
public class GuideWindowDto
{
    /// <summary>Gets or sets the start of the window (UTC).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Gets or sets the end of the window (UTC).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>Gets or sets the server clock, so the client can place "now" without
    /// trusting its own.</summary>
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>Gets the channel rows.</summary>
    public List<GuideChannelDto> Channels { get; } = new();
}

/// <summary>
/// One channel's row in the guide grid.
/// </summary>
public class GuideChannelDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the programming, in order and without gaps between the entries.</summary>
    public List<ProgramDto> Programs { get; } = new();
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

    /// <summary>Gets or sets what kind of thing is on: a program, an interstitial, or nothing.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the program block on air, when one is.</summary>
    public string? BlockName { get; set; }
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

    /// <summary>Gets or sets what is playing: the program, or the trailer filling the gap
    /// before the next one. Null when there is nothing to play.</summary>
    public ProgramDto? Current { get; set; }

    /// <summary>Gets or sets what kind of thing is on: a program, an interstitial, or nothing.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the program block on air, when one is.</summary>
    public string? BlockName { get; set; }

    /// <summary>Gets or sets when what is on now ends (UTC).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>Gets or sets the next program, which is what an interstitial is trailing.</summary>
    public ProgramDto? NextProgram { get; set; }

    /// <summary>Gets or sets the linked trailers for the next program, for a client that can
    /// embed them.</summary>
    public List<TrailerDto> Trailers { get; set; } = new();

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

    /// <summary>Gets or sets what kind of thing this is: Program, Interstitial or OffAir.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the program block it belongs to, when it belongs to one.</summary>
    public string? BlockName { get; set; }

    /// <summary>Gets or sets how far into the item this airing begins - non-zero only where
    /// the end of a block cut the program short and it resumed when the block came round.</summary>
    public long StartOffsetTicks { get; set; }

    /// <summary>Gets or sets the name of the program this leads into.</summary>
    public string? NextProgramName { get; set; }
}
