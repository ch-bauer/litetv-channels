using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelPlaybackUser _playbackUser;
    private readonly YouTubeStreamResolver _trailers;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiteTvController"/> class.
    /// </summary>
    /// <param name="guide">The channel guide.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playbackUser">The account channel playback runs under.</param>
    /// <param name="trailers">Resolves linked trailers into playable streams.</param>
    /// <param name="httpClientFactory">Fetches artwork chosen from somewhere else.</param>
    public LiteTvController(
        ChannelGuide guide,
        ILibraryManager libraryManager,
        ChannelPlaybackUser playbackUser,
        YouTubeStreamResolver trailers,
        IHttpClientFactory httpClientFactory)
    {
        _guide = guide;
        _libraryManager = libraryManager;
        _playbackUser = playbackUser;
        _trailers = trailers;
        _httpClientFactory = httpClientFactory;
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
        foreach (var channel in ChannelGuide.Channels())
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

        // Two lists, deliberately. "What is next" is always a program - a viewer asking that
        // does not mean the advert break - but the schedule a guide draws is the whole
        // schedule, breaks included, or the times in it do not add up. A client that wants to
        // see the trailers land where the plugin actually put them asks for them.
        var following = window.Skip(1).Where(a => a.Kind == AiringKind.Program).ToList();
        var next = following.FirstOrDefault();
        var listed = breaks
            ? window.Skip(1).Where(a => a.Kind == AiringKind.Program || a.Kind == AiringKind.Interstitial).ToList()
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
    /// Serves the configuration page as an ordinary web page.
    /// <para>
    /// The dashboard already has this page; this is the same bytes at an address anything can
    /// link to. It exists for the Plugin Pages plugin, which puts a link in the web client's
    /// own sidebar and loads whatever URL the entry names - so that configuring a channel does
    /// not mean going to Dashboard, Plugins, LiteTV every time.
    /// </para>
    /// <para>
    /// Open, because it is markup: every number on it is fetched afterwards through endpoints
    /// that do check, and the page is useless to anyone those refuse.
    /// </para>
    /// </summary>
    /// <returns>The configuration page.</returns>
    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetPage()
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.LiteTv.Configuration.configPage.html");

        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(AsFragment(reader.ReadToEnd()), "text/html; charset=utf-8");
    }

    /// <summary>
    /// Turns the dashboard's configuration page into something that can be dropped into
    /// another page.
    /// <para>
    /// Two things stop the file being served as it stands. It is a whole document, and the
    /// host appends it as a fragment, so the doctype and the head are noise at best. And its
    /// outermost element carries Jellyfin's own <c>page</c> class, which the web client's CSS
    /// hides until its view manager decides to show it - and here nothing ever will, because
    /// the markup was appended into a container by hand. Left alone the entire page is present
    /// in the DOM and invisible, which is exactly what "the plugin page does not show up"
    /// looks like from the sofa.
    /// </para>
    /// </summary>
    private static string AsFragment(string html)
    {
        const string Open = "<body>";
        const string Close = "</body>";

        var start = html.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
        var end = html.LastIndexOf(Close, StringComparison.OrdinalIgnoreCase);
        var body = start >= 0 && end > start
            ? html.Substring(start + Open.Length, end - start - Open.Length)
            : html;

        return body
            .Replace("data-role=\"page\"", string.Empty, StringComparison.Ordinal)
            .Replace(
                "class=\"page type-interior pluginConfigurationPage\"",
                "class=\"pluginConfigurationPage\"",
                StringComparison.Ordinal);
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
            ? PhysicalFile(path, "image/jpeg")
            : NotFound();
    }

    private static string ArtworkFileName(Guid channelId, string kind) =>
        $"{channelId:N}-{kind.ToLowerInvariant()}.img";

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
                Referer = YouTubeStreamResolver.Referer
            };
        }

        return NotFound();
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
            TrailsItemId = airing.Kind == AiringKind.Interstitial ? airing.NextProgram?.ItemId : null,
            TrailsName = airing.Kind == AiringKind.Interstitial ? airing.NextProgram?.Name : null,
            TrailerUrl = airing.TrailerUrl
        };

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
    /// </summary>
    private static IEnumerable<Guid> ChannelSources(TvChannel channel) =>
        channel.Sources
            .Concat(channel.Blocks.SelectMany(b => b.Sources))
            .Concat(channel.TrailerTitles)
            .Select(s => s.ItemId)
            .Where(id => id != Guid.Empty)
            .Distinct();

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
            cache[itemId] = item = _libraryManager.GetItemById(itemId);
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
        var item = _libraryManager.GetItemById(itemId);

        // An episode almost never has trailers of its own - the providers attach them to the
        // series - so a channel advertising "SpongeBob at 20:15" would find nothing to play
        // for every break it scheduled. The series' trailer is the right answer anyway: what
        // is being advertised is the programme, and nobody cuts a trailer per episode.
        if (item is Episode episode && (item.RemoteTrailers is null || item.RemoteTrailers.Count == 0))
        {
            item = episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) ?? item : item;
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

    /// <summary>Gets or sets the Referer the stream must be requested with.</summary>
    public string Referer { get; set; } = string.Empty;
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
