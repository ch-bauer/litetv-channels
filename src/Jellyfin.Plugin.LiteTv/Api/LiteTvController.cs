using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Integrations;
using Jellyfin.Plugin.LiteTv.Sessions;
using Jellyfin.Plugin.LiteTv.Trailers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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

    /// <summary>The pictures a channel can be given: a wide card, a background, a cover.</summary>
    private static readonly string[] ArtworkKinds = { "banner", "backdrop", "poster" };

    private readonly ChannelGuide _guide;
    private readonly WeekStore _weeks;
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelPlaybackUser _playbackUser;
    private readonly YouTubeStreamResolver _trailers;
    private readonly SponsorBlockClient _sponsorBlock;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SiblingPlugins _siblings;
    private readonly SmartSimilarClient _smartSimilar;
    private readonly YouTubePlaylist _playlists;
    private readonly ChannelStore _channels;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvController"/> class.
    /// </summary>
    /// <param name="guide">The channel guide.</param>
    /// <param name="weeks">The stored weeks.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playbackUser">The account channel playback runs under.</param>
    /// <param name="trailers">Resolves linked trailers into playable streams.</param>
    /// <param name="sponsorBlock">Says which parts of a trailer are not the trailer.</param>
    /// <param name="httpClientFactory">Fetches artwork chosen from somewhere else.</param>
    /// <param name="siblings">Which of the other plugins are installed.</param>
    /// <param name="smartSimilar">Scores suggestions, when that plugin is there.</param>
    /// <param name="playlists">Reads YouTube playlists.</param>
    /// <param name="channels">The channels, a file each.</param>
    public LiteTvController(
        ChannelGuide guide,
        WeekStore weeks,
        ILibraryManager libraryManager,
        ChannelPlaybackUser playbackUser,
        YouTubeStreamResolver trailers,
        SponsorBlockClient sponsorBlock,
        IHttpClientFactory httpClientFactory,
        SiblingPlugins siblings,
        SmartSimilarClient smartSimilar,
        YouTubePlaylist playlists,
        ChannelStore channels)
    {
        _guide = guide;
        _weeks = weeks;
        _libraryManager = libraryManager;
        _playbackUser = playbackUser;
        _trailers = trailers;
        _sponsorBlock = sponsorBlock;
        _httpClientFactory = httpClientFactory;
        _siblings = siblings;
        _smartSimilar = smartSimilar;
        _playlists = playlists;
        _channels = channels;
    }

    /// <summary>
    /// Gets the UI options and all enabled channels with what is on air right now.
    /// </summary>
    /// <returns>The guide payload.</returns>
    [HttpGet("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GuideDto> GetChannels()
    {
        var result = new GuideDto();

        var artwork = NewArtworkCache();
        foreach (var channel in _guide.Channels())
        {
            var window = _guide.Window(channel, DateTime.UtcNow, DateTime.UtcNow.AddHours(DefaultGuideHours)).Take(24).ToList();
            var now = window.FirstOrDefault();
            var nextProgram = window.Skip(1).FirstOrDefault(a => a.Kind == AiringKind.Program);

            result.Channels.Add(new ChannelSummaryDto
            {
                Id = channel.Id,
                Name = channel.Name,
                Curated = _weeks.Has(channel.Id),
                Kind = now?.Kind.ToString() ?? nameof(AiringKind.OffAir),
                BlockName = string.IsNullOrEmpty(now?.BlockName) ? null : now!.BlockName,
                // A break is something the channel is doing, not nothing. Dropping it left the
                // overview saying "off air" every time a gap came round, which is both wrong
                // and alarming; an interstitial describes itself and wears the artwork of what
                // it is advertising. Only genuinely dark air has nothing to show.
                Now = now is null || now.Kind == AiringKind.OffAir ? null : ToProgram(now, artwork),
                Next = nextProgram is null ? null : ToProgram(nextProgram, artwork),
                // The channel's own picture, which is not the same question as what is on.
                // A client drawing a channel card falls back to this when the program on air
                // has no wide artwork - or when nothing is on air at all, which is when the
                // card used to go black.
                Image = ChannelImage(channel, artwork)
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

        var artwork = NewArtworkCache();
        foreach (var channel in _guide.Channels())
        {
            var row = new GuideChannelDto { Id = channel.Id, Name = channel.Name };
            foreach (var airing in _guide.Window(channel, start, end).Take(512))
            {
                row.Programs.Add(ToProgram(airing, artwork));
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
    public ActionResult<ChannelNowDto> GetNow([FromRoute] Guid channelId, [FromQuery] int upcoming = 5, [FromQuery] bool breaks = false)
    {
        var channel = _guide.Channel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var at = DateTime.UtcNow;

        // Anything that has already finished is dropped before anybody looks at the list.
        // A window starts at the airing covering its first moment, and that airing can be
        // rebuilt into several - a break is now adverts, then a trailer, then what is left -
        // so the first thing the schedule hands back is not necessarily the thing that is on.
        // Taken literally it made a channel report an advert that ended two minutes ago.
        var window = _guide.Window(channel, at, at.AddHours(12))
            .Where(a => a.EndUtc > at)
            .Take(256)
            .ToList();

        var current = window.FirstOrDefault();
        if (current is null)
        {
            return NotFound();
        }

        // Two lists, deliberately. "What is next" is always a program - a viewer asking that
        // does not mean the advert break - but the schedule a guide draws is the whole
        // schedule, breaks included, or the times in it do not add up. A client that wants to
        // see the trailers land where the plugin actually put them asks for them.
        var following = window.Skip(1).Where(a => a.Kind == AiringKind.Program).ToList();
        var next = following.FirstOrDefault();
        var listed = breaks
            ? window.Skip(1).Where(a => a.Kind == AiringKind.Program
                || a.Kind == AiringKind.Trailer
                || a.Kind == AiringKind.Interstitial).ToList()
            : following;

        var artwork = NewArtworkCache();
        return new ChannelNowDto
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Kind = current.Kind.ToString(),
            BlockName = string.IsNullOrEmpty(current.BlockName) ? null : current.BlockName,
            Current = current.Entry is null ? null : ToProgram(current, artwork),
            OffsetTicks = current.OffsetAt(at),
            EndUtc = current.EndUtc,
            NextProgram = next is null ? null : ToProgram(next, artwork),
            // What the web client can play over an interstitial the library only knows the
            // address of - the usual case, since trailers are far more often linked than held.
            Trailers = next is null ? new List<TrailerDto>() : RemoteTrailers(next.Entry!.ItemId),
            ServerTimeUtc = at,
            // The same fallback the overview gets. A channel screen with a black background is
            // the commonest way for a genre channel to look broken, and it is at its blackest
            // exactly when a break is on - which is when the screen has nothing else to draw.
            Image = ChannelImage(channel, artwork),
            Upcoming = listed.Take(Math.Clamp(upcoming, 0, 64)).Select(a => ToProgram(a, artwork)).ToList()
        };
    }

    /// <summary>
    /// Gets how long an address actually plays for: the video's own length, less the parts
    /// SponsorBlock says the player will skip.
    /// <para>
    /// This is what replaced typing a number into a box beside the address. The typed number
    /// was a guess at the length and knew nothing about the skipping, so a break built from it
    /// either ran quiet at the end or cut the trailer off. Both halves are answered here, and
    /// the page stores what comes back.
    /// </para>
    /// </summary>
    /// <param name="url">The address - a YouTube link, or anything the resolver knows.</param>
    /// <returns>The lengths, and the segments they were worked out from.</returns>
    /// <summary>
    /// How long this channel takes to play everything once, before it starts over.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The cycle, in ticks and in words.</returns>
    [HttpGet("Channels/{channelId}/Cycle")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CycleDto> GetCycle([FromRoute] Guid channelId)
    {
        var channel = _guide.Channel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var (length, entries) = _guide.Cycle(channel);
        return new CycleDto
        {
            Ticks = length.Ticks,
            Entries = entries,
            Words = CycleWords(length, entries)
        };
    }

    /// <summary>
    /// The cycle said in words. Written here rather than on the page because the page is not
    /// the only thing that will want to say it, and because "months" is the answer that makes
    /// the setting worth having at all.
    /// </summary>
    /// <param name="length">How long one cycle runs.</param>
    /// <param name="entries">How many things are in it.</param>
    /// <returns>A sentence.</returns>
    internal static string CycleWords(TimeSpan length, int entries)
    {
        if (entries == 0 || length <= TimeSpan.Zero)
        {
            return "Nothing to play yet.";
        }

        var how = length.TotalDays >= 60
            ? Math.Round(length.TotalDays / 30.44, 1).ToString(CultureInfo.InvariantCulture) + " months"
            : length.TotalDays >= 2
                ? Math.Round(length.TotalDays, 1).ToString(CultureInfo.InvariantCulture) + " days"
                : length.TotalHours >= 1
                    ? Math.Round(length.TotalHours, 1).ToString(CultureInfo.InvariantCulture) + " hours"
                    : Math.Round(length.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes";

        return "Plays through in " + how + " - "
            + entries.ToString(CultureInfo.InvariantCulture)
            + (entries == 1 ? " thing" : " things") + " - then starts over.";
    }

    /// <summary>
    /// What is in a YouTube playlist, so the page can show it before it is added.
    /// <para>
    /// The plugin could already expand a playlist when a week was laid out, but nothing could
    /// ASK - so adding one from the configuration page put a row called "YouTube playlist" on
    /// the list and gave no sign whether the address was any good. A source nobody can see the
    /// contents of is a source nobody can trust.
    /// </para>
    /// <para>
    /// Nothing is stored: this is a look, and the week is still expanded afresh when it is laid
    /// out, so a playlist that gains a video reaches the channel at the next lay-out.
    /// </para>
    /// </summary>
    /// <param name="url">The playlist address, or a bare playlist id.</param>
    /// <returns>The videos, in the playlist's own order.</returns>
    [HttpGet("YouTubePlaylist")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PlaylistDto>> GetYouTubePlaylist([FromQuery] string? url)
    {
        var id = YouTubePlaylist.PlaylistId(url);
        if (id is null)
        {
            return BadRequest("That is not a playlist address.");
        }

        var playlist = await _playlists.ReadAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
        return new PlaylistDto
        {
            PlaylistId = id,
            Title = playlist.Title,
            Items = playlist.Items.Select(i => new PlaylistItemDto
            {
                VideoId = i.VideoId,
                Title = i.Title,
                Url = i.Url,
                Seconds = i.Seconds
            }).ToList()
        };
    }

    [HttpGet("Duration")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DurationDto>> GetDuration([FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("url is required");
        }

        var videoId = YouTubeStreamResolver.VideoId(url);
        var length = await _trailers.LengthAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);

        // Asked for even when the length could not be established, so the page can say which
        // half failed rather than reporting a flat "could not work it out". This answers with
        // nothing when skipping is switched off, which is right: with no skipping the whole
        // video plays and its length is the whole of it.
        var segments = await SkipSegmentsAsync(url).ConfigureAwait(false);

        var asSegments = segments
            .Select(s => new SponsorBlockClient.Segment(s.StartSeconds, s.EndSeconds, s.Category))
            .ToList();

        var skipped = PlayableLength.SkippedSeconds(length, asSegments);

        return new DurationDto
        {
            VideoId = videoId,
            Title = _trailers.KnownTitle(url),
            LengthSeconds = length,
            SkippedSeconds = (int)Math.Round(skipped, MidpointRounding.AwayFromZero),
            PlayableSeconds = PlayableLength.Of(length, asSegments),
            SkipSegments = segments
        };
    }

    /// <summary>
    /// Gets every channel as it is stored - the definitions the configuration page edits, not
    /// the guide.
    /// <para>
    /// The page used to read these out of the plugin configuration document, and write them
    /// back the same way: the whole list, every time, from a page that might have loaded before
    /// the last change. They are a file each now (<see cref="ChannelStore"/>), so this is where
    /// the page gets them and the two endpoints below are how it changes one.
    /// </para>
    /// </summary>
    /// <returns>The channels.</returns>
    [HttpGet("Definitions")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TvChannel>> GetDefinitions() => Ok(_channels.All());

    /// <summary>
    /// Writes <b>one</b> channel, and nothing else.
    /// <para>
    /// The whole point of the store. A save carries one channel, so a value the server cannot
    /// read fails that channel and leaves the others on the air, and a page that has never
    /// heard of a channel somebody else just made cannot delete it by leaving it out.
    /// </para>
    /// <para>
    /// The id in the route wins over the id in the body: a mismatch is a page bug, and taking
    /// the route's is what keeps it from writing over a different channel.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="channel">The channel as it should now be.</param>
    /// <returns>The channel as stored.</returns>
    [HttpPost("Definitions/{channelId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TvChannel> PutDefinition([FromRoute] Guid channelId, [FromBody] TvChannel channel)
    {
        if (channel is null || channelId == Guid.Empty)
        {
            return BadRequest();
        }

        channel.Id = channelId;
        _channels.Save(channel);
        ForgetUnusedArtwork(channel);
        return Ok(channel);
    }

    /// <summary>
    /// Throws a channel away, and its stored week with it.
    /// <para>
    /// The week is deleted here rather than left to the tidying pass, so the folder is right
    /// the moment the request returns instead of one event later.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Definitions/{channelId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteDefinition([FromRoute] Guid channelId)
    {
        if (!_channels.Delete(channelId))
        {
            return NotFound();
        }

        _weeks.Delete(channelId);
        foreach (var kind in ArtworkKinds)
        {
            DeleteArtworkFile(channelId, kind);
        }

        return NoContent();
    }

    /// <summary>
    /// Gets a channel's stored week: every row of it, with the holes between them filled in
    /// so the timeline and the guide are drawn from the same list.
    /// <para>
    /// A channel that has never been laid out has no week, and says so rather than inventing
    /// one - the configuration page offers to lay one out, which is a thing the owner asks for
    /// rather than something that happens to them.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The week.</returns>
    [HttpGet("Channels/{channelId}/Week")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> GetWeek([FromRoute] Guid channelId)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        return ToWeekDto(channel, _weeks.Get(channelId));
    }

    /// <summary>
    /// Lays a channel's week out afresh from its sources and settings, and stores it.
    /// <para>
    /// <b>This discards whatever curation the week held.</b> It is the wholesale route the
    /// owner takes on purpose; the surgical one is dragging a row. Nothing else in the plugin
    /// calls it - not adding a source, not changing a setting, not saving the configuration
    /// page - because a stored week is the channel's schedule and nothing may quietly rewrite
    /// it.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The week as it now stands.</returns>
    [HttpPost("Channels/{channelId}/Week/Generate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> GenerateWeek([FromRoute] Guid channelId)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        // The length of the schedule survives a re-lay-out: asking for the week again must
        // not quietly turn somebody's fortnight back into a week.
        var week = _guide.GenerateWeek(channel, _weeks.Get(channelId)?.Weeks ?? 1);
        _weeks.Save(week);
        return ToWeekDto(channel, week);
    }

    /// <summary>
    /// Puts something on the timeline, or moves something already on it.
    /// <para>
    /// What is placed is an appointment: it holds its stretch of the week and everything
    /// already there bends around it. A row sent with an id the week already holds is moved
    /// rather than copied.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="airing">The row.</param>
    /// <returns>The week as it now stands.</returns>
    [HttpPut("Channels/{channelId}/Week/Airings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> PutWeekAiring([FromRoute] Guid channelId, [FromBody] WeekAiringDto airing)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var week = _weeks.Get(channelId) ?? new StoredWeek { ChannelId = channelId };
        week.ChannelId = channelId;
        var placed = FromDto(airing);
        placed.DurationSeconds = LengthOf(airing);
        week.Airings = WeekEditing.Place(week.Airings, placed, week.CycleSeconds);
        _weeks.Save(week);
        return ToWeekDto(channel, week);
    }

    /// <summary>
    /// Takes a row off the timeline. What it occupied becomes a hole; nothing slides up to
    /// fill it, because the programme at nine is still at nine.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="airingId">The row.</param>
    /// <returns>The week as it now stands.</returns>
    [HttpDelete("Channels/{channelId}/Week/Airings/{airingId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> DeleteWeekAiring([FromRoute] Guid channelId, [FromRoute] Guid airingId)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var week = _weeks.Get(channelId);
        if (week is null)
        {
            return NotFound();
        }

        week.Airings = WeekEditing.Remove(week.Airings, airingId);
        _weeks.Save(week);
        return ToWeekDto(channel, week);
    }

    /// <summary>
    /// Applies a run of edits to a channel's week, either as a rehearsal or for good.
    /// <para>
    /// The configuration page used to write every schedule edit down the instant it was made,
    /// which is the one thing on that page the Save button did not cover. The owner's verdict
    /// was that the schedule should wait for Save like everything else and be undoable up to
    /// it. That needs the page to hold a list of edits it has not committed - and it cannot
    /// draw them itself, because <b>what an edit does to the rest of the week is the server's
    /// arithmetic</b>: an appointment trims, splits and drops its neighbours, and a page that
    /// guessed at that would draw a week nobody is going to get.
    /// </para>
    /// <para>
    /// So the page sends the whole run and asks what it would come to. With
    /// <paramref name="commit"/> false nothing is written: the answer is a rehearsal, redrawn
    /// after every edit and after every undo. With it true the same run is applied and saved,
    /// which is what Save does.
    /// </para>
    /// <para>
    /// Sending the run rather than a diff is deliberate. It makes undo the removal of the last
    /// element and nothing else, and it means a rehearsal and the commit that follows it are
    /// computed from the same input - so what was on screen is what gets stored.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="commit">True to store the result; false to answer without writing.</param>
    /// <param name="edits">The run of edits, oldest first.</param>
    /// <returns>The week as the run leaves it.</returns>
    [HttpPost("Channels/{channelId}/Week/Edits")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> ApplyWeekEdits(
        [FromRoute] Guid channelId,
        [FromQuery] bool commit,
        [FromBody] WeekEditsDto edits)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        // A COPY. `Get` hands back the cached instance - what the guide and playback are
        // reading - and a rehearsal that walked it in place would change what the channel is
        // airing while claiming to write nothing. Measured on the test server, where asking
        // what a fortnight would look like left the channel a fortnight until the next restart.
        var week = RunEdits(
            _weeks.Get(channelId)?.Copy(),
            channelId,
            edits.Edits,
            weeks => _guide.GenerateWeek(channel, weeks),
            LengthOf,
            () => WeeksForCycle(_guide.Cycle(channel).Length));

        if (commit)
        {
            if (week is null)
            {
                _weeks.Delete(channelId);
            }
            else
            {
                _weeks.Save(week);
            }
        }

        return ToWeekDto(channel, week);
    }

    /// <summary>
    /// Throws a channel's stored week away entirely, putting the channel back to the schedule
    /// its sources and settings describe. Loses the curation, and is only ever reached by the
    /// owner asking for it.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The channel with no week.</returns>
    [HttpDelete("Channels/{channelId}/Week")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WeekDto> DeleteWeek([FromRoute] Guid channelId)
    {
        var channel = ConfiguredChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        _weeks.Delete(channelId);
        return ToWeekDto(channel, null);
    }

    /// <summary>
    /// The longest a schedule may run before it repeats.
    /// <para>
    /// Thirteen weeks - a quarter. Long enough for anything anybody has asked for and short
    /// enough that a stored file and the timeline that draws it stay a reasonable size; a
    /// schedule of a year would be four hundred thousand seconds of arithmetic per read and a
    /// grid nobody could find Thursday in.
    /// </para>
    /// </summary>
    public const int MaximumWeeks = 13;

    /// <summary>
    /// How many weeks a channel needs before its schedule may repeat, given how long it takes
    /// to play everything it has once.
    /// <para>
    /// This is what the length is actually FOR. A channel of every SpongeBob episode should air
    /// all of them and then start again; a week-long schedule airs the first week's worth
    /// forever and never reaches the rest, however many episodes the channel is given. Typing a
    /// number works, but nobody knows what the number is - the server does.
    /// </para>
    /// <para>
    /// Rounded UP, because a schedule shorter than its content cuts the tail off; and a channel
    /// with more content than the cap can hold gets the cap, which airs more of it than a week
    /// ever did.
    /// </para>
    /// </summary>
    /// <param name="length">How long the channel takes to play everything once.</param>
    /// <returns>The number of weeks, between one and <see cref="MaximumWeeks"/>.</returns>
    public static int WeeksForCycle(TimeSpan length)
    {
        var seconds = length.TotalSeconds;
        if (seconds <= 0)
        {
            // Nothing measurable to play. One week, which is what every channel was before.
            return 1;
        }

        var weeks = (int)Math.Ceiling(seconds / StoredWeek.SecondsPerWeek);
        return Math.Clamp(weeks, 1, MaximumWeeks);
    }

    /// <summary>
    /// Folds a run of edits over a stored week.
    /// <para>
    /// Pure but for the two things it is handed: how to lay a week out, and how long a placed
    /// row runs. Both need a library or a clock; the arithmetic does not, and it is the
    /// arithmetic that has to be right - a run applied in the wrong order, or a Place after a
    /// Clear that quietly does nothing, would give the owner a week that is not the one they
    /// were shown.
    /// </para>
    /// </summary>
    /// <param name="stored">The week as it stands, or null when the channel has none.</param>
    /// <param name="channelId">The channel, for a week the run has to create.</param>
    /// <param name="edits">The run, oldest first.</param>
    /// <param name="generate">Lays a schedule out afresh, over the number of weeks it is
    /// handed - so laying out again does not undo a channel's fortnightly schedule.</param>
    /// <param name="fitWeeks">How many weeks the channel's whole content needs. Asked for only
    /// when a <c>FitLength</c> edit is in the run, because working it out walks the channel's
    /// entire schedule.</param>
    /// <param name="lengthOf">How long a placed row runs.</param>
    /// <returns>The week the run leaves, or null when it leaves none.</returns>
    public static StoredWeek? RunEdits(
        StoredWeek? stored,
        Guid channelId,
        IEnumerable<WeekEditDto> edits,
        Func<int, StoredWeek> generate,
        Func<WeekAiringDto, int> lengthOf,
        Func<int>? fitWeeks = null)
    {
        // Null all the way through means "this channel has no stored week", which is a real
        // state and not the same as a week with nothing in it: a channel with no week airs from
        // its sources. Clear returns to it, and a Place after a Clear starts a fresh one.
        var week = stored;

        foreach (var edit in edits)
        {
            switch (edit.Kind?.ToUpperInvariant())
            {
                case "GENERATE":
                    week = generate(week?.Weeks ?? 1);
                    break;

                case "CLEAR":
                    week = null;
                    break;

                case "FITLENGTH":
                    // The same as Length, with the number worked out from the channel's content
                    // instead of typed. Sent with a Generate behind it, or the new weeks are
                    // empty and the channel airs nothing in them.
                    if (week is not null && fitWeeks is not null)
                    {
                        week.Weeks = Math.Clamp(fitWeeks(), 1, MaximumWeeks);
                    }

                    break;

                case "LENGTH":
                    /*
                        How many weeks the schedule runs for before it repeats.

                        Growing it keeps everything: what was the whole schedule becomes its
                        first week, and the new weeks are empty until something is put in them.

                        Shrinking DROPS what falls outside rather than wrapping it round, which
                        is what anyone means by making a fortnight a week again - they mean
                        keep the first one. Wrapping would silently lay week two on top of week
                        one and the schedule would come back a mess nobody asked for.
                    */
                    if (week is not null && edit.Weeks is { } asked)
                    {
                        var wanted = Math.Clamp(asked, 1, MaximumWeeks);
                        if (wanted < week.Weeks)
                        {
                            var keep = wanted * StoredWeek.SecondsPerWeek;
                            week.Airings = week.Airings
                                .Where(a => a.StartSecond < keep)
                                .Select(a =>
                                {
                                    a.DurationSeconds = Math.Min(a.DurationSeconds, keep - a.StartSecond);
                                    return a;
                                })
                                .Where(a => a.DurationSeconds >= WeekEditing.MinimumRemainderSeconds)
                                .ToList();
                        }

                        week.Weeks = wanted;
                    }

                    break;

                case "REMOVE":
                    if (week is not null && edit.AiringId is { } removing)
                    {
                        week.Airings = WeekEditing.Remove(week.Airings, removing);
                    }

                    break;

                case "PLACE":
                    if (edit.Airing is { } airing)
                    {
                        week ??= new StoredWeek { ChannelId = channelId };
                        week.ChannelId = channelId;
                        var placed = FromDto(airing);
                        placed.DurationSeconds = lengthOf(airing);
                        week.Airings = WeekEditing.Place(week.Airings, placed, week.CycleSeconds);
                    }

                    break;

                default:
                    // An edit this build does not know is skipped rather than refused: a page
                    // one release ahead must not be able to make the whole run fail.
                    break;
            }
        }

        return week;
    }

    /// <summary>
    /// A channel as the configuration page means it: by id, whether or not it is on air. The
    /// guide only ever answers for enabled channels, and a channel being edited is often one
    /// that has been switched off precisely because it is being worked on.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The channel, or null.</returns>
    private TvChannel? ConfiguredChannel(Guid channelId)
        => _channels.Get(channelId);

    /// <summary>The stored row a page's payload describes.</summary>
    /// <param name="dto">The payload.</param>
    /// <returns>The row.</returns>
    /// <summary>
    /// What a fallback length is, in seconds, for something dropped onto the week that the
    /// library cannot measure. Half an hour: long enough to see, grab and move, and obviously
    /// a placeholder rather than a claim about the item.
    /// </summary>
    public const int UnknownLengthSeconds = 30 * 60;

    /// <summary>
    /// How long a placed row runs.
    /// <para>
    /// The page deliberately sends nothing when something is dragged onto the week: a typed
    /// length has twice turned out to be a control that lied, and the number that matters is
    /// the item's own runtime. So the length is resolved <b>here</b>, where the library is.
    /// Storing the zero the page sent is how a dragged programme became a hairline nobody
    /// could see, and read as "drag and drop does not work".
    /// </para>
    /// </summary>
    /// <param name="sentSeconds">The length the page sent; zero or less means it did not know.</param>
    /// <param name="runtimeTicks">The library item's runtime, or zero when there is none.</param>
    /// <returns>The length in seconds, always more than nothing.</returns>
    public static int LengthOf(int sentSeconds, long runtimeTicks)
    {
        if (sentSeconds > 0)
        {
            return sentSeconds;
        }

        if (runtimeTicks > 0)
        {
            var seconds = (int)Math.Round(runtimeTicks / (double)TimeSpan.TicksPerSecond);

            // A runtime that rounds to less than the week will keep is no answer: the row would
            // be dropped as a sliver, or drawn as a hairline, which is the fault being fixed.
            if (seconds >= WeekEditing.MinimumRemainderSeconds)
            {
                return seconds;
            }
        }

        // An address, or an item the library never measured. Something visible beats nothing.
        return UnknownLengthSeconds;
    }

    /// <summary>
    /// The same question with the library in hand: how long the row the page sent should run.
    /// </summary>
    /// <param name="airing">The row as the page sent it.</param>
    /// <returns>The length in seconds.</returns>
    private int LengthOf(WeekAiringDto airing)
    {
        var ticks = airing.ItemId is { } itemId && itemId != Guid.Empty
            ? _libraryManager.Find(itemId)?.RunTimeTicks ?? 0
            : 0;
        return LengthOf(airing.DurationSeconds, ticks);
    }

    /// <summary>
    /// Which week of a channel's cycle is airing at this moment, counting from zero.
    /// </summary>
    /// <param name="weeks">How many weeks the cycle runs for.</param>
    /// <returns>The index of the current week.</returns>
    private static int CurrentWeekOf(int weeks)
    {
        if (weeks <= 1)
        {
            return 0;
        }

        var nowLocal = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local),
            DateTimeKind.Unspecified);
        var into = WeekReader.WeekStart(nowLocal) - WeekReader.CycleStart(nowLocal, weeks);
        return (int)Math.Round(into.TotalDays / 7);
    }

    private static StoredAiring FromDto(WeekAiringDto dto)
    {
        return new StoredAiring
        {
            Id = dto.Id ?? Guid.NewGuid(),
            StartSecond = dto.StartSecond,
            DurationSeconds = dto.DurationSeconds,
            Kind = Enum.TryParse<StoredAiringKind>(dto.Kind, ignoreCase: true, out var kind) && kind != StoredAiringKind.Gap
                ? kind
                : StoredAiringKind.Programme,
            ItemId = dto.ItemId ?? Guid.Empty,
            Name = dto.Name ?? string.Empty,
            Url = dto.Url ?? string.Empty,
            OffsetTicks = dto.OffsetTicks,
            SeriesName = dto.SeriesName,
            BlockName = dto.BlockName,
            TrailedItemId = dto.TrailedItemId ?? Guid.Empty,
            TrailedName = dto.TrailedName
        };
    }

    /// <summary>
    /// The week as the page draws it: the stored rows and the holes between them, in order,
    /// each with the stretch of the week it occupies.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="week">The stored week, or null when there is none.</param>
    /// <returns>The payload.</returns>
    private WeekDto ToWeekDto(TvChannel channel, StoredWeek? week)
    {
        var result = new WeekDto
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Curated = week is not null,
            Weeks = week is null ? 1 : Math.Max(1, week.Weeks),
            CurrentWeek = CurrentWeekOf(week is null ? 1 : Math.Max(1, week.Weeks)),
            GeneratedUtc = week?.GeneratedUtc,
            ModifiedUtc = week?.ModifiedUtc
        };

        if (week is null)
        {
            return result;
        }

        foreach (var (row, kind, _) in WeekReader.BuildRows(week))
        {
            result.Airings.Add(new WeekAiringDto
            {
                Id = row.Kind == StoredAiringKind.Gap ? null : row.Id,
                StartSecond = row.StartSecond,
                DurationSeconds = row.DurationSeconds,
                Kind = row.Kind.ToString(),
                ItemId = row.ItemId == Guid.Empty ? null : row.ItemId,
                Name = row.Name,
                Url = row.Url,
                OffsetTicks = row.OffsetTicks,
                SeriesName = row.SeriesName,
                BlockName = row.BlockName,
                TrailedItemId = row.TrailedItemId == Guid.Empty ? null : row.TrailedItemId,
                TrailedName = row.TrailedName,
                OffAir = kind == AiringKind.OffAir
            });
        }

        return result;
    }

    /// <summary>
    /// Stores a picture for a channel, uploaded from the configuration page.
    /// <para>
    /// A channel is not a library item, so there is nowhere in Jellyfin to put its artwork.
    /// The alternative - pointing every channel at an address somewhere else - works right up
    /// until the address stops working, which for a picture nobody else is looking after is a
    /// matter of when. The plugin keeps the bytes itself.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel the picture belongs to.</param>
    /// <param name="kind">Which picture: banner, backdrop or poster.</param>
    /// <returns>The address the picture is now served from.</returns>
    [HttpPost("Artwork/{channelId}/{kind}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<UploadedArtworkDto>> PostArtwork([FromRoute] Guid channelId, [FromRoute] string kind)
    {
        if (!ArtworkKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest("kind must be banner, backdrop or poster");
        }

        var directory = ArtworkDirectory();
        if (directory is null)
        {
            return BadRequest("the plugin has nowhere to store artwork");
        }

        Directory.CreateDirectory(directory);

        // One file per channel and kind, overwritten. Keeping the old ones would mean deciding
        // when to delete them, and a picture nobody can see any more is only clutter.
        var path = Path.Combine(directory, ArtworkFileName(channelId, kind));
        await using (var file = System.IO.File.Create(path))
        {
            await Request.Body.CopyToAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        return new UploadedArtworkDto { Url = $"/LiteTv/Artwork/{channelId}/{kind.ToLowerInvariant()}" };
    }

    /// <summary>
    /// Fetches a picture from somewhere else and keeps it.
    /// <para>
    /// A picture chosen from a metadata provider is an address on somebody else's server, and
    /// an address nobody here is looking after stops working sooner or later - at which point
    /// the channel is a black rectangle again and nothing says why. A television on the far
    /// side of a household firewall may not reach it even while it works. Downloading it once
    /// makes the choice permanent and local, which is what choosing a picture ought to mean.
    /// </para>
    /// <para>
    /// Addresses on this server are fetched too, deliberately: a copy stops following the
    /// library item, which is the point when somebody re-scrapes a series and does not want
    /// the channel's face to change with it.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel the picture belongs to.</param>
    /// <param name="kind">Which picture: banner, backdrop or poster.</param>
    /// <param name="request">The address to fetch.</param>
    /// <returns>The address the picture is now served from.</returns>
    [HttpPost("Artwork/{channelId}/{kind}/Fetch")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UploadedArtworkDto>> FetchArtwork(
        [FromRoute] Guid channelId,
        [FromRoute] string kind,
        [FromBody] FetchArtworkDto request)
    {
        if (!ArtworkKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest("kind must be banner, backdrop or poster");
        }

        var directory = ArtworkDirectory();
        if (directory is null)
        {
            return BadRequest("the plugin has nowhere to store artwork");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("give an http or https address");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return BadRequest($"the address answered {(int)response.StatusCode}");
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest($"that address is {type}, not a picture");
            }

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ArtworkFileName(channelId, kind));

            // Written whole and moved into place, so a fetch that dies half way leaves the
            // picture that was already there rather than a truncated file the client will
            // draw as nothing.
            var temporary = path + ".part";
            await using (var file = System.IO.File.Create(temporary))
            {
                await response.Content.CopyToAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            System.IO.File.Move(temporary, path, overwrite: true);
            return new UploadedArtworkDto { Url = $"/LiteTv/Artwork/{channelId}/{kind.ToLowerInvariant()}" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return BadRequest($"could not fetch that picture: {ex.Message}");
        }
    }

    /// <summary>
    /// Serves a channel picture.
    /// <para>
    /// Deliberately open, like Jellyfin's own item images. A television client draws these
    /// through an image loader that carries no credentials - the same reason
    /// <c>/Items/{id}/Images</c> is open - and a channel logo is not the library.
    /// </para>
    /// </summary>
    /// <param name="channelId">The channel the picture belongs to.</param>
    /// <param name="kind">Which picture: banner, backdrop or poster.</param>
    /// <returns>The image, or 404 when the channel has none of that kind.</returns>
    [HttpGet("Artwork/{channelId}/{kind}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetArtwork([FromRoute] Guid channelId, [FromRoute] string kind)
    {
        var directory = ArtworkDirectory();
        if (directory is null || !ArtworkKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var path = Path.Combine(directory, ArtworkFileName(channelId, kind));
        return System.IO.File.Exists(path)
            ? PhysicalFile(path, ArtworkContentType(path))
            : NotFound();
    }

    private static string ArtworkContentType(string path)
    {
        Span<byte> header = stackalloc byte[12];
        try
        {
            using var file = System.IO.File.OpenRead(path);
            var read = file.Read(header);
            if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                return "image/png";
            }

            if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (read >= 6 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
            {
                return "image/gif";
            }

            if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
            {
                return "image/webp";
            }
        }
        catch (IOException)
        {
        }

        return "application/octet-stream";
    }

    private static string ArtworkFileName(Guid channelId, string kind) =>
        $"{channelId:N}-{kind.ToLowerInvariant()}.img";

    /// <summary>
    /// Throws away any uploaded picture this channel no longer points at.
    /// <para>
    /// An upload writes a file and hands the page an address; clearing the slot, or replacing
    /// the picture with a borrowed one or an address somewhere else, only changed the address.
    /// The file stayed on disk with nothing referring to it, and there was no way to remove it
    /// short of reaching the box - which is how a crop test left a Spongebob banner sitting in
    /// the plugin folder for a week.
    /// </para>
    /// <para>
    /// Done on the SAVE rather than when the slot is cleared, and that is the whole reason it
    /// lives here. A page clears a slot long before anybody presses Save, and deleting the file
    /// then would destroy a picture the stored channel still points at the moment the edit is
    /// abandoned. What is stored is the only safe thing to compare against.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel as it has just been stored.</param>
    private static void ForgetUnusedArtwork(TvChannel channel)
    {
        foreach (var kind in UnusedArtwork(channel))
        {
            DeleteArtworkFile(channel.Id, kind);
        }
    }

    /// <summary>
    /// Which of a channel's three uploaded pictures nothing points at any more.
    /// <para>
    /// Separated from the deleting so the decision can be tested without a plugin folder to
    /// delete things out of, which is the only interesting half.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel as stored.</param>
    /// <returns>The kinds whose file, if there is one, is now an orphan.</returns>
    internal static IReadOnlyList<string> UnusedArtwork(TvChannel channel)
    {
        var unused = new List<string>();
        foreach (var kind in ArtworkKinds)
        {
            var url = kind switch
            {
                "banner" => channel.Artwork?.BannerUrl,
                "backdrop" => channel.Artwork?.BackdropUrl,
                _ => channel.Artwork?.PosterUrl
            };

            // Kept only while the channel still points at OUR file for this slot. Any other
            // address - a library item, somewhere else entirely, or nothing at all - means the
            // upload is no longer in use. Compared as a prefix, because the page appends a
            // cache-buster to the address it has just uploaded to.
            var ours = $"/LiteTv/Artwork/{channel.Id:N}/{kind}";
            if (url is null || !url.StartsWith(ours, StringComparison.OrdinalIgnoreCase))
            {
                unused.Add(kind);
            }
        }

        return unused;
    }

    /// <summary>
    /// Removes one stored picture, and does not mind if it was never there.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="kind">Which of the three pictures.</param>
    private static void DeleteArtworkFile(Guid channelId, string kind)
    {
        var directory = ArtworkDirectory();
        if (directory is null)
        {
            return;
        }

        var path = Path.Combine(directory, ArtworkFileName(channelId, kind));
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A picture that will not delete is not worth failing a save over: the channel is
            // already stored and correct, and the file is only wasted space.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? ArtworkDirectory()
    {
        var data = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrEmpty(data) ? null : Path.Combine(data, "artwork");
    }

    /// <summary>
    /// Gets the credentials a client should play a channel with.
    /// <para>
    /// Channel playback runs as an account of the plugin's own, because the server decides
    /// whose watch state a playback belongs to from the token the request carries. A client
    /// asks once, then uses the token it gets back for everything it does to play the
    /// schedule - resolving playback info, fetching the stream, and reporting progress. Not
    /// for browsing: what the viewer sees should still come from their own account.
    /// </para>
    /// <para>
    /// A client that cannot get these credentials must refuse to start the channel. Playing
    /// anyway would record the whole schedule against the viewer's account, which is the one
    /// thing this plugin exists to prevent.
    /// </para>
    /// </summary>
    /// <returns>The credentials, or 503 when the account could not be prepared.</returns>
    [HttpGet("PlaybackUser")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ChannelCredentials>> GetPlaybackUser()
    {
        var credentials = await _playbackUser.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return credentials is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable)
            : credentials;
    }

    /// <summary>
    /// Resolves a trailer for an item into something a player can be handed.
    /// <para>
    /// Almost every trailer a library has is a YouTube link rather than a file, and a
    /// television app cannot open one - Jellyfin's own clients hand it to whatever app claims
    /// the link, which leaves the channel behind in another application. This turns the link
    /// into a stream URL, so a trailer is just another thing the player plays.
    /// </para>
    /// <para>
    /// Ask for it <b>before</b> the break arrives. Resolution reaches out to several services
    /// in turn and can take seconds, which a schedule cannot absorb at a programme boundary;
    /// the client knows what is coming next long before it needs it. When this answers 404 the
    /// client should show a card - "20:15 Avatar" over the artwork - rather than dead air.
    /// </para>
    /// </summary>
    /// <param name="itemId">The item whose trailer is wanted.</param>
    /// <returns>The resolved trailer, or 404 when there is none or it could not be resolved.</returns>
    [HttpGet("Trailer/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResolvedTrailerDto>> GetTrailer([FromRoute] Guid itemId)
    {
        foreach (var trailer in RemoteTrailers(itemId))
        {
            var stream = await _trailers.ResolveAsync(trailer.Url, HttpContext.RequestAborted).ConfigureAwait(false);
            if (stream is null || string.IsNullOrEmpty(stream.Url))
            {
                // Try the next one rather than giving up: a video pulled down or blocked in
                // this region fails on its own, and the second-best trailer still airs.
                continue;
            }

            return new ResolvedTrailerDto
            {
                Name = trailer.Name,
                Url = stream.Url,
                AudioUrl = stream.AudioUrl,
                UserAgent = YouTubeStreamResolver.UserAgent,
                Referer = YouTubeStreamResolver.Referer,
                Client = stream.Client,
                Quality = stream.Quality,
                SkipSegments = await SkipSegmentsAsync(trailer.Url).ConfigureAwait(false)
            };
        }

        return NotFound();
    }

    /// <summary>
    /// Turns an address the schedule named into something a player can be handed.
    /// <para>
    /// The trailer endpoint above starts from a library item and looks up what the metadata
    /// providers linked to it. A break can also name an address outright - an advert, a bumper,
    /// a clip that is not in the library at all - and there is no item behind those to start
    /// from. The resolving is identical; only where the address comes from differs.
    /// </para>
    /// </summary>
    /// <param name="url">The address to resolve, as the schedule gave it.</param>
    /// <returns>The stream to play, or 404 when it cannot be resolved.</returns>
    [HttpGet("Resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResolvedTrailerDto>> GetResolved([FromQuery] string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("give an http or https address");
        }

        var stream = await _trailers.ResolveAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
        if (stream is null || string.IsNullOrEmpty(stream.Url))
        {
            return NotFound();
        }

        return new ResolvedTrailerDto
        {
            Name = string.Empty,
            Url = stream.Url,
            AudioUrl = stream.AudioUrl,
            UserAgent = YouTubeStreamResolver.UserAgent,
            Referer = YouTubeStreamResolver.Referer,
            Client = stream.Client,
            Quality = stream.Quality,
            SkipSegments = await SkipSegmentsAsync(url).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Takes a proof-of-origin token that a television has minted.
    /// <para>
    /// The server cannot mint one: it needs Google's BotGuard, which is JavaScript that wants a
    /// browser. An Android box has one built in, so it runs BotGuard in a WebView and posts the
    /// result here, and every trailer resolved afterwards carries it. See
    /// <see cref="ProofOfOrigin"/> for what it buys - in short, the difference between a 1080p
    /// stream that stops after a minute and one that plays to the end.
    /// </para>
    /// </summary>
    /// <param name="minted">What the television minted.</param>
    /// <returns>What is now held, without the tokens themselves.</returns>
    [HttpPost("PoToken")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PoTokenStatusDto> PostPoToken([FromBody] PoTokenDto minted)
    {
        if (string.IsNullOrWhiteSpace(minted.VisitorData) || string.IsNullOrWhiteSpace(minted.PoToken))
        {
            return BadRequest("visitorData and poToken are both required");
        }

        return Status(ProofOfOrigin.Take(minted.VisitorData, minted.PoToken, minted.PlayerPoToken));
    }

    /// <summary>
    /// Says whether a usable token is held, so the television can decide whether to mint again
    /// and so a person with curl can tell what the server is working with.
    /// </summary>
    /// <returns>The status. Never the tokens - they are a proof of identity.</returns>
    [HttpGet("PoToken")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PoTokenStatusDto> GetPoToken() => Status(ProofOfOrigin.Held);

    /// <summary>
    /// Which clients the trailer resolver can pretend to be. The configuration page offers
    /// these instead of asking for a name to be typed.
    /// </summary>
    /// <returns>The client names, in the order they are tried.</returns>
    [HttpGet("YouTubeClients")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetYouTubeClients()
        => Ok(YouTubeStreamResolver.ClientNames());

    private static PoTokenStatusDto Status(ProofOfOrigin.Minted? held)
    {
        var last = YouTubeStreamResolver.Last;

        return new PoTokenStatusDto
        {
            Held = held is not null,
            TokenState = TokenState(held, ProofOfOrigin.Stored),
            MintedUtc = held?.MintedUtc,
            AgeSeconds = ProofOfOrigin.Stored is { } stored ? SafeAgeSeconds(stored.MintedUtc) : null,
            HasPlayerToken = held?.PlayerToken is not null,
            LastResolved = LastResolvedWords(last),
            LastResolvedLow = last?.Low ?? false
        };
    }

    private static string TokenState(ProofOfOrigin.Minted? held, ProofOfOrigin.Minted? stored)
        => held is not null ? "held" : stored is not null ? "expired" : "missing";

    private static int SafeAgeSeconds(DateTime mintedUtc)
    {
        var age = (DateTime.UtcNow - DateTime.SpecifyKind(mintedUtc, DateTimeKind.Utc)).TotalSeconds;
        return (int)Math.Clamp(age, 0, int.MaxValue);
    }

    /// <summary>
    /// The last resolution, said in words.
    /// <para>
    /// It names whether a token was held <b>at the time</b>, not now, because that is the whole
    /// point: a resolution made before a television minted one is capped, and the reading only
    /// makes sense next to that fact. Anything cached from before a mint stops counting the
    /// instant it happens - the cache is keyed on the token generation - so a low reading with a
    /// token now held means the next play will be better, and a low reading WITH a token held at
    /// the time is a real fault worth chasing.
    /// </para>
    /// </summary>
    /// <param name="last">The last resolution, or null.</param>
    /// <returns>A sentence, or null when nothing has been resolved.</returns>
    internal static string? LastResolvedWords(YouTubeStreamResolver.Resolution? last)
    {
        if (last is null || string.IsNullOrWhiteSpace(last.VideoId) || last.WhenUtc == default)
        {
            return null;
        }

        var quality = last.Quality > 0 && last.Quality < 10000
            ? last.Quality.ToString(CultureInfo.InvariantCulture) + "p"
            : "unknown quality";

        var client = string.IsNullOrWhiteSpace(last.Client) ? "unknown client" : last.Client;

        var token = last.TokenHeld
            ? "with a token"
            : "with no token — a mint would improve this";

        return quality + " · " + client + " · " + token;
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
            _channels.All().Select(c => c.Name),
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

    private static Dictionary<Guid, BaseItem?> NewArtworkCache() => new();

    private ProgramDto ToProgram(Airing airing, Dictionary<Guid, BaseItem?> artwork)
    {
        var entry = airing.Entry;
        var dto = new ProgramDto
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
            NextProgramName = airing.NextProgram?.Name,
            // Named, not inferred. A break advertises one particular programme - usually not
            // the one that starts when the break ends, but one a few slots further on - and a
            // client filling the break has to be told which. Working it out from what follows
            // is right until the schedule does something ordinary, like putting two breaks
            // together or ending the window on one.
            TrailsItemId = airing.Kind == AiringKind.Trailer
                ? entry?.TrailerForItemId
                : airing.Kind == AiringKind.Interstitial ? airing.NextProgram?.ItemId : null,
            TrailsName = airing.Kind == AiringKind.Trailer
                ? entry?.TrailerForName
                : airing.Kind == AiringKind.Interstitial ? airing.NextProgram?.Name : null,
            // A playlist video is a programme, not a preview. Keep its playback address in the
            // programme field; TrailerUrl is reserved for actual interstitials so clients such
            // as Wholphin do not label every YouTube item "Preview".
            PlayUrl = entry?.Url,
            TrailerUrl = airing.Kind == AiringKind.Interstitial ? airing.PlayUrl : null,
            IsYouTube = entry?.IsAddress == true
        };

        if (entry?.IsAddress == true && YouTubeStreamResolver.VideoId(entry.Url) is { Length: > 0 } videoId)
        {
            dto.ImageUrl = "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg";
        }

        // An interstitial wears the artwork of the programme it is trailing, which is what a
        // trailer looks like on television anyway. A dark stretch has nothing to wear.
        var subject = entry ?? airing.NextProgram;
        if (subject is not null)
        {
            ApplyArtwork(dto, subject, artwork);
        }

        return dto;
    }

    /// <summary>
    /// Names the artwork a programme can be drawn with. Only images that actually exist are
    /// named: a client that guessed would draw a broken rectangle for every item whose
    /// library entry happens not to have that particular image, which is what left so much of
    /// the guide blank. An episode falls back to its series, because a series poster is what
    /// a viewer recognises a programme by when the episode itself has no still.
    /// </summary>
    private void ApplyArtwork(ProgramDto dto, ScheduledEntry entry, Dictionary<Guid, BaseItem?> cache)
    {
        var item = Artwork(cache, entry.ItemId);
        var series = entry.SeriesId is { } seriesId && seriesId != Guid.Empty ? Artwork(cache, seriesId) : null;

        // The picture a guide row draws. It wants one wide, specific picture of this
        // programme - and which image that is depends on what the programme is.
        //
        // An episode's Primary *is* its still: already the right shape, and the one thing that
        // tells two episodes of the same series apart. A film's Primary is its upright poster,
        // and cropping that into a wide row is what left the films looking wrong next to the
        // episodes. A film's Thumb - "Miniaturansicht" - is the same artwork drawn wide, which
        // is exactly what the row is asking for.
        //
        // Primary stays last rather than being dropped: it is the one image nearly everything
        // has, and a badly-shaped picture of the right programme beats an empty rectangle.
        // Wide artwork, in the order a row would like it. Primary is last here because for
        // everything except an episode it is the upright one.
        var wideOrder = new[] { ImageType.Thumb, ImageType.Backdrop, ImageType.Primary };

        // The episode's own still is asked for on its own first. Rolled into one call it would
        // fall through to the *series* Primary - an upright poster - before ever reaching the
        // series' Thumb, because Pick tries every item for a type before moving to the next
        // one. That trades one rung of specificity for the wrong shape, which is the whole
        // thing being fixed here.
        var chosen = item is Episode
            ? Pick(new[] { ImageType.Primary }, item) ?? Pick(wideOrder, item, series)
            : Pick(wideOrder, item, series);

        if (chosen is { } poster)
        {
            dto.PosterItemId = poster.ItemId;
            dto.PosterType = poster.Type.ToString();
        }

        // Landscape: what a wide card wants. Falls back to the portrait image rather than
        // leaving the card empty - a card with the wrong shape still says what is on.
        if (Pick(new[] { ImageType.Thumb, ImageType.Backdrop }, item, series) is { } wide)
        {
            dto.BackdropItemId = wide.ItemId;
            dto.BackdropType = wide.Type.ToString();
        }
        else
        {
            dto.BackdropItemId = dto.PosterItemId;
            dto.BackdropType = dto.PosterType;
        }
    }

    /// <summary>
    /// Names the artwork that stands for the channel itself.
    /// <para>
    /// Three sources in order. An address the channel was configured with wins, because
    /// someone chose it. Failing that, a library item the channel was pointed at, whose
    /// artwork it borrows and goes on borrowing when the item is re-scraped. Failing both,
    /// the first thing in the channel's own lineup that has a picture at all - which is how a
    /// channel nobody configured still has a face.
    /// </para>
    /// <para>
    /// This is why it exists: a channel built from one series wears what is on air perfectly
    /// well, and a channel built from a genre does not. What is on changes every hour, much of
    /// it has no wide artwork, and during a break there is nothing on to borrow from at all.
    /// </para>
    /// </summary>
    private ChannelImageDto ChannelImage(TvChannel channel, Dictionary<Guid, BaseItem?> cache)
    {
        var configured = channel.Artwork ?? new ChannelArtwork();
        var dto = new ChannelImageDto
        {
            BannerUrl = NullIfBlank(configured.BannerUrl),
            BackdropUrl = NullIfBlank(configured.BackdropUrl),
            PosterUrl = NullIfBlank(configured.PosterUrl)
        };

        // A named item fills whatever it can; the scan below tops up anything it did not have,
        // so pointing a channel at a series that has no backdrop still gets it a backdrop.
        if (configured.ImageItemId != Guid.Empty
            && FillChannelImage(dto, Artwork(cache, configured.ImageItemId), null))
        {
            return dto;
        }

        // Taken from what the channel is *made of*, not from what it happens to be showing.
        // Read off the schedule it changed every time the programme did - a channel's face
        // flickering between films as the clock moved, which is not a face at all. The sources
        // are the channel's own definition and only change when somebody edits it.
        //
        // Kept going rather than stopped at the first hit: the three pictures are three
        // different shapes and one item rarely has all of them - a series with a banner and no
        // backdrop is ordinary - so the scan takes what each source can give and stops when the
        // channel has a full set, or when the sources run out.
        foreach (var source in ChannelSources(channel))
        {
            if (FillChannelImage(dto, Artwork(cache, source), null))
            {
                break;
            }
        }

        return dto;
    }

    /// <summary>
    /// Every library item a channel is built from, in the order it was configured: the channel's
    /// own lineup first, then whatever its blocks air, then the titles it advertises.
    /// <para>
    /// A collection is followed into its contents. A BoxSet is a name with a list behind it and
    /// very often has no artwork of its own, so a channel built from one - "Marathon: Fünf
    /// Freunde Filmreihe" - had nothing to derive a picture from and came out blank, while the
    /// three films it airs each had a poster and a thumb. The collection is still asked first:
    /// somebody who made artwork for the collection meant it for exactly this.
    /// </para>
    /// </summary>
    private IEnumerable<Guid> ChannelSources(TvChannel channel)
    {
        var seen = new HashSet<Guid>();

        foreach (var id in channel.Sources
                     .Concat(channel.Blocks.SelectMany(b => b.Sources))
                     .Concat(channel.TrailerTitles)
                     .Select(s => s.ItemId)
                     .Where(id => id != Guid.Empty))
        {
            if (seen.Add(id))
            {
                yield return id;
            }

            if (_libraryManager.Find(id) is not BoxSet boxSet)
            {
                continue;
            }

            foreach (var child in boxSet.GetLinkedChildren().OrderBy(c => c.PremiereDate ?? DateTime.MaxValue))
            {
                if (seen.Add(child.Id))
                {
                    yield return child.Id;
                }
            }
        }
    }

    /// <summary>
    /// Puts whatever artwork the given items have onto the channel image, and says whether the
    /// channel now has a full set.
    /// <para>
    /// Three pictures, because a client draws three different things and they are not
    /// interchangeable. A banner is made to be looked at with a name written over it, which is
    /// what a channel card is. A backdrop is the only artwork actually drawn to fill a screen.
    /// A cover is the upright one. Answering all three with the same file - which is what
    /// happened while there was only one wide slot - makes the channel screen a blown-up
    /// version of the card that led to it.
    /// </para>
    /// <para>
    /// The series is asked before the item throughout. For a channel these stand for the
    /// channel rather than for whatever is on it, and an episode's own artwork is a picture of
    /// one scene: its Primary is a 16:9 still, which is neither upright nor the channel.
    /// </para>
    /// </summary>
    private static bool FillChannelImage(ChannelImageDto dto, BaseItem? item, BaseItem? series)
    {
        if (Pick(new[] { ImageType.Primary }, series, item) is { } poster)
        {
            dto.PosterItemId ??= poster.ItemId;
            dto.PosterType ??= poster.Type.ToString();
        }

        if (Pick(new[] { ImageType.Banner, ImageType.Thumb }, series, item) is { } banner)
        {
            dto.BannerItemId ??= banner.ItemId;
            dto.BannerType ??= banner.Type.ToString();
        }

        if (Pick(new[] { ImageType.Backdrop, ImageType.Thumb }, series, item) is { } wide)
        {
            dto.BackdropItemId ??= wide.ItemId;
            dto.BackdropType ??= wide.Type.ToString();
        }

        return dto.PosterItemId is not null && dto.BannerItemId is not null && dto.BackdropItemId is not null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Gets the first of the wanted image types that one of the items actually has, the item
    /// itself taking precedence over the series it belongs to.
    /// </summary>
    private static (Guid ItemId, ImageType Type)? Pick(ImageType[] types, params BaseItem?[] items)
    {
        foreach (var type in types)
        {
            foreach (var item in items)
            {
                if (item is not null && item.GetImageInfo(type, 0) is not null)
                {
                    return (item.Id, type);
                }
            }
        }

        return null;
    }

    private BaseItem? Artwork(Dictionary<Guid, BaseItem?> cache, Guid itemId)
    {
        if (!cache.TryGetValue(itemId, out var item))
        {
            cache[itemId] = item = _libraryManager.Find(itemId);
        }

        return item;
    }

    /// <summary>
    /// Gets the trailers the library only holds an address for - almost always YouTube,
    /// which is what the metadata providers fill in. Nothing the server can schedule, but
    /// the web client can embed them, and that is what fills an interstitial in practice.
    /// </summary>
    private List<TrailerDto> RemoteTrailers(Guid itemId)
    {
        var item = _libraryManager.Find(itemId);

        // An episode almost never has trailers of its own - the providers attach them to the
        // series - so a channel advertising "SpongeBob at 20:15" would find nothing to play
        // for every break it scheduled. The series' trailer is the right answer anyway: what
        // is being advertised is the programme, and nobody cuts a trailer per episode.
        if (item is Episode episode && (item.RemoteTrailers is null || item.RemoteTrailers.Count == 0))
        {
            item = episode.SeriesId != Guid.Empty ? _libraryManager.Find(episode.SeriesId) ?? item : item;
        }

        return item?.RemoteTrailers
            .Where(t => !string.IsNullOrEmpty(t.Url))
            .Select(t => new TrailerDto { Name = t.Name ?? string.Empty, Url = t.Url })
            .OrderBy(TrailerRank)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList() ?? new List<TrailerDto>();
    }

    /// <summary>
    /// Orders trailers the way a viewer would choose: the official trailer first, a teaser
    /// last. A provider lists whatever it found in whatever order it found it, so without
    /// this a channel's break can open with a fifteen-second teaser for a film it is about
    /// to show in full. Borrowed from Wholphin, which sorts its own trailer picker this way.
    /// </summary>
    /// <summary>
    /// The parts of a trailer to skip over, or nothing when the lookup is switched off or has
    /// no answer. Never throws: a trailer with an unskipped sponsor read is a much smaller
    /// problem than a break that fails.
    /// </summary>
    private async Task<List<SkipSegmentDto>> SkipSegmentsAsync(string url)
    {
        if (Plugin.Instance?.Configuration.SkipTrailerSegments != true)
        {
            return new List<SkipSegmentDto>();
        }

        var segments = await _sponsorBlock
            .SegmentsAsync(YouTubeStreamResolver.VideoId(url), HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return segments
            .Select(s => new SkipSegmentDto
            {
                StartSeconds = s.StartSeconds,
                EndSeconds = s.EndSeconds,
                Category = s.Category
            })
            .ToList();
    }

    private static int TrailerRank(TrailerDto trailer)
    {
        // Language first, and by a distance nothing else can close: a German household being
        // told about tonight's film in English is worse than being told about it by a teaser.
        // Which trailer it is only decides between trailers that are already in the language.
        return (GermanRank(trailer.Name) * 100) + KindRank(trailer.Name);
    }

    /// <summary>
    /// Guesses a trailer's language from its name, because that is all there is to go on -
    /// <c>RemoteTrailer</c> carries a name and a URL and nothing else.
    /// <para>
    /// The markers are taken from what the providers actually wrote for this library:
    /// "Trailer Deutsch HD", "Trailer German", "Deutsch Trailer", "offizieller Kinotrailer
    /// german". A German trailer says so in its title nearly every time, because it was
    /// uploaded to be found by people searching in German.
    /// </para>
    /// <para>
    /// "Offizieller" and "Kinotrailer" count on their own: they are German words, and a
    /// trailer titled in German is in German. <c>OmU</c> deliberately does not - it means the
    /// original soundtrack with subtitles, which nobody reads off a television across a room.
    /// </para>
    /// </summary>
    private static int GermanRank(string name)
    {
        foreach (var marker in new[] { "deutsch", "german", "offizieller", "kinotrailer" })
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
        }

        return 1;
    }

    /// <summary>Which kind of trailer it is: the full one, then anything, then a teaser.</summary>
    private static int KindRank(string name)
    {
        if (name.Contains("Official Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Official Theatrical Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Offizieller Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kinotrailer", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("Teaser", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return name.Contains("Trailer", StringComparison.OrdinalIgnoreCase) ? 1 : 5;
    }

    /// <summary>
    /// Reports which of the plugins LiteTV can lean on are installed, and in what state.
    /// One place for it: the configuration page draws the strip from this, and the TV app
    /// asks the same question rather than interrogating Jellyfin itself.
    /// </summary>
    /// <returns>One row per known plugin, installed or not.</returns>
    [HttpGet("Plugins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SiblingPluginStatus>> GetPlugins()
    {
        return Ok(_siblings.All());
    }

    /// <summary>
    /// Scores the library against a handful of chosen titles, for the suggestions screens.
    /// The scoring itself belongs to the Smart Similar plugin - LiteTV asks it rather than
    /// carrying a second copy of it - and falls back to a deliberately rough genre match
    /// when that plugin is not installed. The answer always says which of the two replied.
    /// </summary>
    /// <param name="itemIds">Comma-separated seed item ids.</param>
    /// <param name="userId">The user whose library access applies; empty for all of it.</param>
    /// <param name="minScore">Floor on the score, or null for Smart Similar's own setting.</param>
    /// <param name="limit">Maximum results, default 40.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored pool.</returns>
    [HttpGet("Suggestions/Scored")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScoredSuggestionsDto>> GetScoredSuggestions(
        [FromQuery] string? itemIds,
        [FromQuery] Guid userId,
        [FromQuery] int? minScore,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var seeds = ParseItemIds(itemIds);
        var result = new ScoredSuggestionsDto
        {
            SmartSimilar = _siblings.All().FirstOrDefault(
                p => string.Equals(p.Id, SiblingPlugins.SmartSimilarId.ToString(), StringComparison.OrdinalIgnoreCase))
        };

        if (seeds.Count == 0)
        {
            result.Engine = "None";
            return Ok(result);
        }

        int max = Math.Clamp(limit ?? 40, 1, 200);

        var scored = await _smartSimilar.ScoreAsync(
            new Uri(Request.Scheme + "://" + Request.Host.Value),
            Request.Headers.Authorization.ToString(),
            Request.Headers["X-Emby-Token"].ToString(),
            seeds,
            userId,
            minScore,
            max,
            cancellationToken).ConfigureAwait(false);

        if (scored != null && scored.Active)
        {
            result.Engine = "SmartSimilar";
            foreach (var seed in scored.Seeds)
            {
                result.Seeds.Add(new SuggestionSeedDto
                {
                    Id = seed.Id,
                    Name = seed.Name,
                    Kind = seed.Kind,
                    Active = seed.Active,
                    Source = seed.Source
                });
            }

            foreach (var match in scored.Results)
            {
                var item = _libraryManager.Find(match.Id);
                if (item == null)
                {
                    continue;
                }

                var dto = Describe(item, match.Score);
                dto.SharedGenres = match.Shared?.Genres.ToList() ?? new List<string>();
                dto.SharedPeople = match.Shared?.People.ToList() ?? new List<string>();
                dto.SharedTags = match.Shared?.Tags.ToList() ?? new List<string>();
                dto.SharedStudios = match.Shared?.Studios.ToList() ?? new List<string>();
                dto.YearGap = match.Shared?.YearGap;
                dto.SameOfficialRating = match.Shared?.OfficialRating ?? false;
                dto.PerSeed = match.PerSeed.ToList();
                result.Results.Add(dto);
            }

            return Ok(result);
        }

        // No Smart Similar, or it could not answer. Say so, and answer roughly.
        result.Engine = "Rough";

        var seedItems = seeds
            .Select(id => _libraryManager.Find(id))
            .OfType<BaseItem>()
            .Where(item => item is Movie or Series)
            .ToList();

        foreach (var seed in seedItems)
        {
            result.Seeds.Add(new SuggestionSeedDto
            {
                Id = seed.Id,
                Name = seed.Name ?? string.Empty,
                Kind = seed is Series ? "Series" : "Movie",
                Active = true,
                Source = "Rough"
            });
        }

        if (seedItems.Count == 0)
        {
            return Ok(result);
        }

        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        });

        var ranked = RoughSimilarity.Rank(
            seedItems.Select(ToSimilarityInput).ToList(),
            candidates.Select(ToSimilarityInput),
            minScore ?? 15,
            max);

        foreach (var match in ranked)
        {
            var item = _libraryManager.Find(match.Id);
            if (item == null)
            {
                continue;
            }

            var dto = Describe(item, match.Score);
            dto.SharedGenres = match.SharedGenres.ToList();
            dto.YearGap = match.YearGap;
            result.Results.Add(dto);
        }

        return Ok(result);
    }

    private static SimilarityInput ToSimilarityInput(BaseItem item)
    {
        return new SimilarityInput(
            item.Id,
            item is Series ? "Series" : "Movie",
            item.Genres ?? Array.Empty<string>(),
            item.ProductionYear,
            item.CommunityRating);
    }

    private static SuggestionMatchDto Describe(BaseItem item, double score)
    {
        return new SuggestionMatchDto
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Kind = item is Series ? "Series" : "Movie",
            Year = item.ProductionYear,
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            Score = score
        };
    }

    private static List<Guid> ParseItemIds(string? itemIds)
    {
        var ids = new List<Guid>();
        if (string.IsNullOrWhiteSpace(itemIds))
        {
            return ids;
        }

        foreach (var part in itemIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Configuration guids come back dash-less; Guid.TryParse takes either spelling.
            if (Guid.TryParse(part, out var id) && id != Guid.Empty && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

/// <summary>
/// A linked trailer, resolved to something playable.
/// </summary>
public class ResolvedTrailerDto
{
    /// <summary>Gets or sets the trailer name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream address to play.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the audio address, when the trailer's audio is a stream of its own.
    /// <para>
    /// Null means <see cref="Url"/> carries its own sound. When it is set, the two have to be
    /// played together - that pairing is the only way YouTube offers anything above 360p, so a
    /// client that ignores this field gets a picture in silence rather than a poor trailer.
    /// </para>
    /// </summary>
    public string? AudioUrl { get; set; }

    /// <summary>Gets or sets the User-Agent the stream must be requested with.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which YouTube client answered, so a trailer that looks wrong on a
    /// television can be explained without reading the server log.
    /// </summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Gets or sets the height in pixels of what was resolved, or 0 when unknown.</summary>
    public int Quality { get; set; }

    /// <summary>Gets or sets the Referer the stream must be requested with.</summary>
    public string Referer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stretches of the trailer that are not the trailer, in order.
    /// <para>
    /// A client is expected to seek straight past them without asking. The skip button the
    /// fork shows for library content is right for a programme and wrong here: a break is a
    /// minute long, and nobody should have to press a button inside an advert.
    /// </para>
    /// </summary>
    public List<SkipSegmentDto> SkipSegments { get; set; } = new();
}

/// <summary>
/// A stretch of a trailer to seek past: an uploader's branded card, a plea to subscribe, a
/// read for something unrelated.
/// </summary>
public class SkipSegmentDto
{
    /// <summary>Gets or sets where it starts, in seconds from the beginning.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets where it ends, and where playback should carry on from.</summary>
    public double EndSeconds { get; set; }

    /// <summary>Gets or sets what SponsorBlock calls it - "sponsor", "intro", "outro".</summary>
    public string Category { get; set; } = string.Empty;
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

    /// <summary>
    /// Gets or sets a value indicating whether this channel has a week of its own. A channel
    /// without one airs whatever its content and settings work out to, which is a different
    /// thing to say about it on a page than what is on right now.
    /// </summary>
    public bool Curated { get; set; }

    /// <summary>Gets or sets the artwork standing for the channel itself, which a client falls
    /// back to when what is on air has no picture worth drawing - or when nothing is on air at
    /// all.</summary>
    public ChannelImageDto Image { get; set; } = new();
}

/// <summary>
/// An address to fetch a channel picture from.
/// </summary>
public class FetchArtworkDto
{
    /// <summary>Gets or sets the address.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Where an uploaded channel picture ended up.
/// </summary>
public class UploadedArtworkDto
{
    /// <summary>Gets or sets the address the picture is served from.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// The pictures a channel is drawn with, as addresses and as library artwork.
/// </summary>
public class ChannelImageDto
{
    /// <summary>Gets or sets the address of the wide picture for a channel card.</summary>
    public string? BannerUrl { get; set; }

    /// <summary>Gets or sets the address of the full-screen background.</summary>
    public string? BackdropUrl { get; set; }

    /// <summary>Gets or sets the address of the upright cover.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the item holding the channel's upright artwork.</summary>
    public Guid? PosterItemId { get; set; }

    /// <summary>Gets or sets the image type to ask <see cref="PosterItemId"/> for.</summary>
    public string? PosterType { get; set; }

    /// <summary>Gets or sets the item holding the channel's card artwork - wide, with room
    /// for a name over it.</summary>
    public Guid? BannerItemId { get; set; }

    /// <summary>Gets or sets the image type to ask <see cref="BannerItemId"/> for.</summary>
    public string? BannerType { get; set; }

    /// <summary>Gets or sets the item holding the channel's full-screen artwork.</summary>
    public Guid? BackdropItemId { get; set; }

    /// <summary>Gets or sets the image type to ask <see cref="BackdropItemId"/> for.</summary>
    public string? BackdropType { get; set; }
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

    /// <summary>Gets or sets the artwork standing for the channel itself, drawn when what is
    /// on air has no picture of its own.</summary>
    public ChannelImageDto Image { get; set; } = new();
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

    /// <summary>Gets or sets the item this break is advertising. Set on an interstitial only.
    /// Usually not the programme that follows the break - television trails what is on later,
    /// not what is about to start - so a client must use this rather than looking at what comes
    /// next. Its start time can be read off the same guide by finding this id.</summary>
    public Guid? TrailsItemId { get; set; }

    /// <summary>Gets or sets the name of the programme this break is advertising.</summary>
    public string? TrailsName { get; set; }

    /// <summary>Gets or sets the address to play in this interstitial, when the schedule names
    /// one outright rather than leaving the client to find a trailer for the next programme.</summary>
    public string? TrailerUrl { get; set; }

    /// <summary>Gets or sets the direct playback address of a programme without a library item.</summary>
    public string? PlayUrl { get; set; }

    /// <summary>Gets or sets whether this programme came from a YouTube source.</summary>
    public bool IsYouTube { get; set; }

    /// <summary>Gets or sets a stable thumbnail address for a YouTube programme.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the item to draw the portrait image from - the programme itself,
    /// or the series it belongs to. Null when neither has one.</summary>
    public Guid? PosterItemId { get; set; }

    /// <summary>Gets or sets the image type to ask <see cref="PosterItemId"/> for.</summary>
    public string? PosterType { get; set; }

    /// <summary>Gets or sets the item to draw the landscape image from.</summary>
    public Guid? BackdropItemId { get; set; }

    /// <summary>Gets or sets the image type to ask <see cref="BackdropItemId"/> for.</summary>
    public string? BackdropType { get; set; }
}

/// <summary>
/// The channel behind an item a session is playing.
/// </summary>
public class PlayingChannelDto
{
    /// <summary>Gets or sets the channel id.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether episodes watched by carrying on with
    /// a series, rather than following the schedule, are shielded like everything else the
    /// channel plays.</summary>
    public bool ShieldBingedEpisodes { get; set; } = true;
}

/// <summary>
/// A proof-of-origin token as a television mints it.
/// </summary>
public class PoTokenDto
{
    /// <summary>
    /// Gets or sets the visitor id the token was minted against.
    /// <para>
    /// Not optional and not cosmetic: the token is bound to this, and a request carrying one
    /// without the other is refused in a way that looks exactly like carrying neither.
    /// </para>
    /// </summary>
    public string VisitorData { get; set; } = string.Empty;

    /// <summary>Gets or sets the token for stream addresses.</summary>
    public string PoToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token for the player request, when the minter made a separate one.
    /// Leave it empty rather than repeating <see cref="PoToken"/> - the two are different
    /// contexts, and the wrong one is worse than none.
    /// </summary>
    public string? PlayerPoToken { get; set; }
}

/// <summary>
/// What the server is holding. Deliberately says nothing about the token itself.
/// </summary>
public class PoTokenStatusDto
{
    /// <summary>Gets or sets a value indicating whether a usable token is held.</summary>
    public bool Held { get; set; }

    /// <summary>Gets or sets the independent persisted-token state: held, expired, or missing.</summary>
    public string TokenState { get; set; } = "missing";

    /// <summary>Gets or sets when it was minted.</summary>
    public DateTime? MintedUtc { get; set; }

    /// <summary>Gets or sets how long ago that was, in seconds.</summary>
    public int? AgeSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether a separate player token came with it.</summary>
    public bool HasPlayerToken { get; set; }

    /// <summary>
    /// Gets or sets what the last resolution actually produced, in words - or null when this
    /// server has not resolved anything since it started.
    /// <para>
    /// Without this, a channel serving 360p to the whole house and one serving a 1080p ladder
    /// look identical: both simply play.
    /// </para>
    /// </summary>
    public string? LastResolved { get; set; }

    /// <summary>Gets or sets a value indicating whether that last resolution came out low.</summary>
    public bool LastResolvedLow { get; set; }
}

/// <summary>
/// A channel's stored week, as the configuration page draws it.
/// </summary>
public class WeekDto
{
    /// <summary>Gets or sets the channel.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this channel has a stored week at all. False
    /// means nobody has laid one out, and the channel is still airing whatever its sources and
    /// settings say - which the page has to be able to tell the owner, because laying one out
    /// is a thing they choose.
    /// </summary>
    public bool Curated { get; set; }

    /// <summary>
    /// Gets or sets how many weeks the schedule runs for before it repeats. One for every
    /// channel that has never been told otherwise, and the page draws one week at a time
    /// whatever this says.
    /// </summary>
    public int Weeks { get; set; } = 1;

    /// <summary>
    /// Gets or sets which week of the cycle is on now, counting from zero.
    /// <para>
    /// Answered by the server because only the server knows: which week of a fortnight is
    /// running is counted from a fixed Monday, and a page working it out for itself would be
    /// a second implementation of the one piece of arithmetic that must not disagree.
    /// </para>
    /// </summary>
    public int CurrentWeek { get; set; }

    /// <summary>Gets or sets when the week was last laid out by the generator.</summary>
    public DateTime? GeneratedUtc { get; set; }

    /// <summary>Gets or sets when it last changed at all.</summary>
    public DateTime? ModifiedUtc { get; set; }

    /// <summary>Gets the rows, in order, holes included.</summary>
    public List<WeekAiringDto> Airings { get; } = new();
}

/// <summary>
/// One row of a stored week, over the wire.
/// </summary>
/// <summary>
/// What a YouTube playlist holds, as the configuration page asks for it.
/// </summary>
public class PlaylistDto
{
    /// <summary>Gets or sets the playlist's own id.</summary>
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what YouTube calls the playlist. Empty when it would not say.
    /// <para>
    /// The page names the source with this. It used to compose one - "16 videos - &lt;the first
    /// video's title&gt;" - and the schedule carried that under every programme as its series,
    /// so a channel read as two contradictory titles and got reported as a wrong schedule.
    /// </para>
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the videos, in the playlist's order.</summary>
    public IReadOnlyList<PlaylistItemDto> Items { get; set; } = Array.Empty<PlaylistItemDto>();
}

/// <summary>
/// One video in a playlist.
/// </summary>
public class PlaylistItemDto
{
    /// <summary>Gets or sets the video's id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets its title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a watchable address for it.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets how long it runs, or zero when the page did not say.</summary>
    public int Seconds { get; set; }
}

/// <summary>
/// A run of schedule edits the page has made and not yet committed.
/// </summary>
public class WeekEditsDto
{
    /// <summary>
    /// Gets or sets the edits, oldest first. Applied in order to the stored week; the last one
    /// is the one an undo takes off.
    /// <para>
    /// <b>The setter is load-bearing.</b> Every other collection on these DTOs is get-only,
    /// which is right because every other one is only ever serialised OUT. This one is
    /// deserialised IN, and System.Text.Json cannot fill a collection it has no way to assign:
    /// it skips the property and hands the action an empty list. Nothing fails - the run is
    /// simply empty, every rehearsal answers with the week exactly as stored, and every edit
    /// appears on screen and then undoes itself. That shipped in 1.0.77.0.
    /// </para>
    /// </summary>
    public List<WeekEditDto> Edits { get; set; } = new();
}

/// <summary>
/// One schedule edit.
/// </summary>
public class WeekEditDto
{
    /// <summary>
    /// Gets or sets what the edit is: <c>Place</c>, <c>Remove</c>, <c>Generate</c> or
    /// <c>Clear</c>. Read case-insensitively, and an unknown kind is skipped rather than
    /// refused.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the row being placed or moved. Only read by <c>Place</c>.</summary>
    public WeekAiringDto? Airing { get; set; }

    /// <summary>Gets or sets the row being taken off. Only read by <c>Remove</c>.</summary>
    public Guid? AiringId { get; set; }

    /// <summary>
    /// Gets or sets how many weeks the schedule should run for. Only read by <c>Length</c>.
    /// </summary>
    public int? Weeks { get; set; }
}

public class WeekAiringDto
{
    /// <summary>
    /// Gets or sets the row's id. Null for a hole in the week, which is not stored and so has
    /// nothing to address - that is also how the page knows not to let one be dragged.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>Gets or sets when it starts, in seconds after Monday 00:00 local.</summary>
    public int StartSecond { get; set; }

    /// <summary>Gets or sets how long it runs, in seconds.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Gets or sets what it is: Programme, Trailer, Advert or Gap.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the library item, when there is one.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets what the guide calls it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the address to play, for something the library only links to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets how far into the item the row starts.</summary>
    public long OffsetTicks { get; set; }

    /// <summary>Gets or sets the series name, when the item is an episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the block the row came from when the week was laid out.</summary>
    public string? BlockName { get; set; }

    /// <summary>Gets or sets the programme a break is announcing.</summary>
    public Guid? TrailedItemId { get; set; }

    /// <summary>Gets or sets that programme's name.</summary>
    public string? TrailedName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a hole this long counts as the channel being
    /// off air rather than between programmes. Worked out on the server so the page and the
    /// guide cannot disagree about it.
    /// </summary>
    public bool OffAir { get; set; }
}

/// <summary>
/// How long a channel takes to play everything once.
/// </summary>
public class CycleDto
{
    /// <summary>Gets or sets the length of one full cycle, in ticks.</summary>
    public long Ticks { get; set; }

    /// <summary>Gets or sets how many entries are in it.</summary>
    public int Entries { get; set; }

    /// <summary>Gets or sets the same thing said in words.</summary>
    public string Words { get; set; } = string.Empty;
}

/// <summary>
/// How long an address plays for, and what was taken out of it.
/// </summary>
public class DurationDto
{
    /// <summary>Gets or sets the YouTube video id, when the address is one.</summary>
    public string? VideoId { get; set; }

    /// <summary>
    /// Gets or sets what YouTube calls the video, in the language the server asks in - see
    /// <see cref="Trailers.YouTubeLocale"/>. Null when YouTube would not say.
    /// <para>
    /// Here so an advert nobody typed a name for is not listed by its video id. The break card
    /// read "aqz-KE-bpKQ", which tells nobody anything, and the title arrives on the same
    /// answer as the length so it costs no extra request.
    /// </para>
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the video's own length in seconds, or zero when YouTube would not say -
    /// which the page has to be able to tell apart from "it is very short".
    /// </summary>
    public int LengthSeconds { get; set; }

    /// <summary>Gets or sets how much of it the player will skip over.</summary>
    public int SkippedSeconds { get; set; }

    /// <summary>
    /// Gets or sets what is left, and what the schedule should give it. Zero when the length
    /// is unknown, or when the skips leave too little behind to believe.
    /// </summary>
    public int PlayableSeconds { get; set; }

    /// <summary>Gets or sets the segments the skipping is worked out from.</summary>
    public List<SkipSegmentDto> SkipSegments { get; set; } = new();
}

/// <summary>
/// The scored pool behind a suggestions screen, and which engine produced it.
/// </summary>
public class ScoredSuggestionsDto
{
    /// <summary>
    /// Gets or sets what answered: "SmartSimilar", "Rough" when that plugin is absent or
    /// silent, or "None" when nothing was asked for. The screen shows this rather than
    /// pretending the two are the same.
    /// </summary>
    public string Engine { get; set; } = "None";

    /// <summary>Gets or sets the Smart Similar plugin's state, for the strip on the page.</summary>
    public SiblingPluginStatus? SmartSimilar { get; set; }

    /// <summary>Gets the seeds as the engine understood them.</summary>
    public List<SuggestionSeedDto> Seeds { get; } = new();

    /// <summary>Gets the ranked candidates.</summary>
    public List<SuggestionMatchDto> Results { get; } = new();
}

/// <summary>
/// One title the suggestions were built from.
/// </summary>
public class SuggestionSeedDto
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets "Movie" or "Series".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this seed could be scored at all.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets which engine answered for it: Local, Tmdb, Hybrid or Rough.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// One suggested title, with enough of itself to be drawn and enough of the reasoning
/// to be explained.
/// </summary>
public class SuggestionMatchDto
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets "Movie" or "Series".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets its production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets its community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets its age rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the mean score over the comparable seeds, 0-100.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets the score against each seed, in the order they were sent.</summary>
    public List<double?> PerSeed { get; set; } = new();

    /// <summary>Gets or sets the genres it shares with the seeds.</summary>
    public List<string> SharedGenres { get; set; } = new();

    /// <summary>Gets or sets the tags it shares. Empty under the rough engine, which has none.</summary>
    public List<string> SharedTags { get; set; } = new();

    /// <summary>Gets or sets the directors, writers and actors it shares. Empty under the rough engine.</summary>
    public List<string> SharedPeople { get; set; } = new();

    /// <summary>Gets or sets the studios it shares. Empty under the rough engine.</summary>
    public List<string> SharedStudios { get; set; } = new();

    /// <summary>Gets or sets how many years separate it from the closest seed.</summary>
    public int? YearGap { get; set; }

    /// <summary>Gets or sets a value indicating whether it carries the same age rating as a seed.</summary>
    public bool SameOfficialRating { get; set; }
}
