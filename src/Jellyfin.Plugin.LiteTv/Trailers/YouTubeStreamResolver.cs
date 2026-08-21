using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Turns a YouTube link into a URL a player can actually be handed.
/// <para>
/// <b>What the answer is good for, measured 21 Aug 2026.</b> An adaptive URL will serve any
/// window of a megabyte or so, at any offset - but only while it is fresh, and never the whole
/// file at once. A URL a few minutes old answers 403 to everything past its first window; a
/// request for tens of megabytes is refused outright even on a URL seconds old. So the cache
/// here is two minutes rather than forty-five, and a client is expected to read in windows.
/// Muxed streams have neither restriction and are the floor when adaptive cannot be had.
/// </para>
/// <para>
/// Most of a library's trailers are links rather than files - here, 15 of 19 films have only
/// a YouTube <c>RemoteTrailer</c> and not one has a local trailer - so without this a channel's
/// trailer breaks are silence. A television app cannot open a YouTube page, and handing the
/// link to whatever app claims it (which is what Jellyfin's own clients do) leaves the channel
/// behind in another application.
/// </para>
/// <para>
/// The approach is Moonfin's, which is the only client observed doing this successfully: ask
/// for the video's stream URL as though we were one of YouTube's own players, then play that
/// URL like any other. Three sources are tried in turn, because the first is the fastest and
/// the least reliable.
/// </para>
/// <para>
/// <b>This is grey and it will break.</b> It leans on undocumented endpoints, hardcoded keys
/// and public mirrors that come and go. The ladder is the mitigation, not a fix: when every
/// rung fails the caller gets nothing back and is expected to show a card instead, which is
/// why nothing here throws.
/// </para>
/// </summary>
public sealed class YouTubeStreamResolver
{
    /// <summary>
    /// What was resolved, and how - the how being reportable so that field testing on a
    /// television does not have to be done by reading the server log.
    /// </summary>
    /// <param name="Url">The stream to play.</param>
    /// <param name="AudioUrl">Its audio, when the picture is a stream of its own.</param>
    /// <param name="Client">Which client answered.</param>
    /// <param name="Quality">The height in pixels, or 0 when nothing said.</param>
    public sealed record ResolvedStream(string Url, string? AudioUrl, string Client = "", int Quality = 0);

    /// <summary>
    /// How long a resolved URL is offered again before it is looked up afresh.
    /// <para>
    /// Two minutes, and it used to be forty-five. The <c>expire</c> on a googlevideo URL is
    /// hours away and means nothing: measured on 21 Aug 2026, an adaptive URL a few minutes
    /// old answers 403 to everything past its first window while a fresh one serves any window
    /// asked for. Resolution costs a fifth of a second, so there is nothing to protect - the
    /// cache exists now only to stop a break asking twice while it is starting.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The height at which the ladder stops looking. Below it the answer is kept but the next
    /// rung is still asked.
    /// <para>
    /// This is the whole of the 360p fix. Innertube's <c>formats</c> list - the muxed one,
    /// video and audio in a single file - nowadays holds itag 18 and little else, so a client
    /// that answers without a manifest hands back 360p and the old ladder stopped there
    /// satisfied. ANDROID leads the ladder because it is the most likely to answer at all, and
    /// it is precisely the one that tends not to return a manifest; IOS and the TV player
    /// usually do. Carrying on costs a couple of fast requests and gets the quality ladder.
    /// </para>
    /// </summary>
    private const int GoodEnough = 720;

    /// <summary>
    /// The quality a manifest counts as: above anything a single file can score, since it
    /// carries every rendition the video has.
    /// </summary>
    private const int Manifest = int.MaxValue;

    /// <summary>
    /// Sent when the stream is fetched as well as when it is resolved. Google serves a
    /// different answer, or none, without them.
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";

    /// <summary>The referer the stream must be requested with.</summary>
    public const string Referer = "https://www.youtube.com/";

    private static readonly string[] PipedInstances =
    {
        "https://pipedapi.kavin.rocks",
        "https://pipedapi.moomoo.me"
    };

    private static readonly string[] InvidiousInstances =
    {
        "https://invidious.fdn.fr",
        "https://invidious.privacyredirect.com",
        "https://invidious.projectsegfau.lt"
    };

    /// <summary>
    /// The clients we claim to be, in the order they are tried.
    /// <para>
    /// <b>No API key.</b> Every port of this trick carries a table of hardcoded
    /// <c>AIzaSy...</c> keys copied from YouTube's own players, and on 21 Aug 2026 the player
    /// endpoint was measured answering identically with and without one. They were a liability
    /// with no upside: they look like credentials in a public repository, they belong to
    /// somebody else, and they are precisely the sort of constant that is rotated without
    /// notice. What actually identifies the caller is the client name, its version and the
    /// matching User-Agent, which is all that is sent now - the same shape yt-dlp settled on.
    /// </para>
    /// <para>
    /// <b>Copied field by field from ReVanced on 22 Aug 2026.</b> Their GitHub answers 451 now;
    /// the repository is on GitLab, at <c>gitlab.com/revanced/revanced-patches</c>, and this was
    /// read from <c>extensions/shared/library/.../spoof/ClientType.java</c> and
    /// <c>spoof/requests/PlayerRoutes.java</c> there. <b>Read the real one, not a fork</b> - the
    /// Morphe fork has diverged (its VR client is a Pico 4 on Android 10, and it sends the
    /// client <i>name</i> in a header where ReVanced sends the id) and following it would have
    /// been wrong twice over.
    /// </para>
    /// <para>
    /// Theirs is a list of five and this is those five. The device profiles were already right
    /// here; what was wrong was the shape of the request around them - see
    /// <see cref="AndroidUserAgent"/> for the User-Agent, <see cref="Context"/> for the field
    /// that should never have been sent, and <see cref="AppPlayerEndpoint"/> for the endpoint.
    /// </para>
    /// <para>
    /// <b>What cannot be copied is the part that logs in.</b> ReVanced does not build an
    /// <c>Authorization</c> header at all: it forwards the one the real YouTube app already
    /// holds, along with <c>X-Goog-Visitor-Id</c> and <c>X-GOOG-API-FORMAT-VERSION</c>, and it
    /// <i>skips</i> any <c>useAuth</c> client outright when the user is not logged in. That is
    /// an app OAuth token belonging to a signed-in phone, which a Jellyfin server does not have
    /// and cannot mint - and it is a different thing entirely from the cookie signature this
    /// plugin can build, which is measured to buy nothing (see the configuration note on
    /// <c>YouTubeCookie</c>).
    /// </para>
    /// </summary>
    private static readonly InnertubeClient[] Clients =
    {
        // ANDROID_VR 1.61.48 - Oculus Quest 3, Android 12, SDK 32, build SQ3A.220605.009.A1.
        // ReVanced: "This client can only be used when logged out", useAuth false - so nothing
        // is ever signed for it. It leads here rather than in their order because it
        // is the one measured returning every rendition up to 2160p with an address on each.
        new("ANDROID_VR", "28", "1.61.48")
        {
            PackageName = "com.google.android.apps.youtube.vr.oculus",
            DeviceMake = "Oculus",
            DeviceModel = "Quest 3",
            OsName = "Android",
            OsVersion = "12",
            AndroidSdkVersion = 32,
            BuildId = "SQ3A.220605.009.A1"
        },

        // ANDROID_VR 1.43.32 - the same headset, an older app. ReVanced carries both, and it
        // costs one fast failure to have a second try at the client that pays best.
        new("ANDROID_VR", "28", "1.43.32")
        {
            PackageName = "com.google.android.apps.youtube.vr.oculus",
            DeviceMake = "Oculus",
            DeviceModel = "Quest 3",
            OsName = "Android",
            OsVersion = "12",
            AndroidSdkVersion = 32,
            BuildId = "SQ3A.220605.009.A1"
        },

        // ANDROID_REEL - the plain Android app, and the client ReVanced notes "has been used by
        // most open-source YouTube stream extraction tools since 2024". Two things are theirs
        // and unusual: it is the one client that does NOT use the player endpoint - it asks
        // reel/reel_item_watch with the request nested under playerRequest - and they warn that
        // sending an access token with it helps Google identify the caller as ReVanced.
        //
        // The device fields are Build.MANUFACTURER/MODEL/RELEASE/SDK_INT/ID on a real phone.
        // A server has no such thing, so a plausible phone is hardcoded; that is the one place
        // this cannot be verbatim.
        new("ANDROID", "3", "20.44.38")
        {
            PackageName = "com.google.android.youtube",
            DeviceMake = "Google",
            DeviceModel = "Pixel 9 Pro Fold",
            OsName = "Android",
            OsVersion = "15",
            AndroidSdkVersion = 35,
            BuildId = "AP3A.241005.015.A2",
            UsePlayerEndpoint = false
        },

        // VISIONOS - "Internal YT client for an unreleased YT client. May stop working at any
        // time," in their words. Not an Android client, so its User-Agent is given rather than
        // generated, and it sends no androidSdkVersion.
        new("VISIONOS", "101", "0.1")
        {
            DeviceMake = "Apple",
            DeviceModel = "RealityDevice14,1",
            OsName = "visionOS",
            OsVersion = "1.3.21O771",
            UserAgentOverride =
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 "
                + "(KHTML, like Gecko) Version/18.0 Safari/605.1.15"
        },

        // Ours, not ReVanced's: they dropped WEB from streaming entirely. Kept last because on
        // 22 Aug 2026 it still answered on this server with 360p, and a rung that answers is
        // worth more than a tidy list. It is the only client here that talks to the website
        // rather than the app endpoint.
        new("WEB", "1", "2.20250312.04.00")
        {
            Browser = true,
            UserAgentOverride = UserAgent
        }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeStreamResolver> _logger;
    private readonly ConcurrentDictionary<string, CachedStream> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeStreamResolver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeStreamResolver(IHttpClientFactory httpClientFactory, ILogger<YouTubeStreamResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Pulls the video id out of the YouTube link shapes a metadata provider writes.
    /// </summary>
    /// <param name="url">The trailer address.</param>
    /// <returns>The video id, or null when this is not a YouTube link.</returns>
    public static string? VideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Contains("youtu.be", StringComparison.Ordinal))
        {
            var first = uri.Segments.Skip(1).FirstOrDefault()?.Trim('/');
            return string.IsNullOrEmpty(first) ? null : first;
        }

        if (!host.Contains("youtube.com", StringComparison.Ordinal)
            && !host.Contains("youtube-nocookie.com", StringComparison.Ordinal))
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var v = query["v"];
        if (!string.IsNullOrEmpty(v))
        {
            return v;
        }

        // /embed/ID, /shorts/ID and /v/ID all name the video in the path instead.
        var parts = uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToList();
        for (var i = 0; i < parts.Count - 1; i++)
        {
            if (parts[i] is "embed" or "shorts" or "v")
            {
                return parts[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a trailer address to something playable.
    /// </summary>
    /// <param name="url">The trailer address as the library holds it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to play, or null when it could not be resolved. A link that is not
    /// YouTube is handed back untouched, since it may already be playable.</returns>
    public async Task<ResolvedStream?> ResolveAsync(string? url, CancellationToken cancellationToken)
    {
        var id = VideoId(url);
        if (id is null)
        {
            return string.IsNullOrEmpty(url) ? null : new ResolvedStream(url, null);
        }

        if (_cache.TryGetValue(id, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Stream;
        }

        var resolved = await ResolveIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            _cache[id] = new CachedStream(resolved, DateTime.UtcNow.Add(CacheFor));
        }

        return resolved;
    }

    private async Task<ResolvedStream?> ResolveIdAsync(string id, CancellationToken cancellationToken)
    {
        StreamCandidate? best = null;

        StreamCandidate? Better(StreamCandidate? candidate)
        {
            if (candidate is not null && (best is null || candidate.Quality > best.Quality))
            {
                best = candidate;
            }

            return best is { Quality: >= GoodEnough } ? best : null;
        }

        // One client only, when somebody testing on a television has named one. What YouTube
        // gives back depends on who is asking, and that cannot be settled from here - see
        // PluginConfiguration.YouTubeClient.
        var only = Plugin.Instance?.Configuration.YouTubeClient;
        var ladder = string.IsNullOrWhiteSpace(only)
            ? Clients
            : Clients.Where(c => string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var client in ladder.Length == 0 ? Clients : ladder)
        {
            var found = await TryInnertubeAsync(id, client, cancellationToken).ConfigureAwait(false);
            if (Better(found) is { } good)
            {
                _logger.LogDebug(
                    "LiteTV: resolved trailer {Id} as {Client}, {Quality}",
                    id,
                    good.Source,
                    Describe(good));
                return good.Stream;
            }
        }

        foreach (var instance in PipedInstances)
        {
            var found = await TryMirrorAsync($"{instance}/streams/{id}", "hls", "videoStreams", instance, cancellationToken)
                .ConfigureAwait(false);
            if (Better(found) is { } good)
            {
                _logger.LogDebug(
                    "LiteTV: resolved trailer {Id} via {Instance}, {Quality}",
                    id,
                    good.Source,
                    Describe(good));
                return good.Stream;
            }
        }

        foreach (var instance in InvidiousInstances)
        {
            var found = await TryMirrorAsync($"{instance}/api/v1/videos/{id}", null, "formatStreams", instance, cancellationToken)
                .ConfigureAwait(false);
            if (Better(found) is { } good)
            {
                _logger.LogDebug(
                    "LiteTV: resolved trailer {Id} via {Instance}, {Quality}",
                    id,
                    good.Source,
                    Describe(good));
                return good.Stream;
            }
        }

        if (best is not null)
        {
            // Nothing anywhere offered better, so the low one is the trailer. Said out loud
            // because a channel quietly airing 360p is exactly the complaint this ladder
            // exists to answer.
            _logger.LogInformation(
                "LiteTV: trailer {Id} resolved only to {Quality}, from {Source}",
                id,
                Describe(best),
                best.Source);
            return best.Stream;
        }

        _logger.LogInformation("LiteTV: no source could resolve trailer {Id}", id);
        return null;
    }

    /// <summary>
    /// The client half of an Innertube request: who this is pretending to be, in the shape
    /// YouTube expects it. The device fields are only sent when the client has them, because an
    /// empty deviceModel is worse than no deviceModel.
    /// </summary>
    /// <summary>
    /// Unwraps the reel endpoint's answer, which nests the whole player response one level
    /// deeper. The player endpoint's answer is already at the top and is handed back untouched.
    /// </summary>
    /// <param name="root">The parsed response.</param>
    /// <returns>The player response.</returns>
    private static JsonElement PlayerResponse(JsonElement root) =>
        root.TryGetProperty("playerResponse", out var nested) ? nested : root;

    /// <summary>
    /// The User-Agent an Android client sends, built the way ReVanced builds it.
    /// <para>
    /// Theirs is one format string in <c>ClientType</c>'s Android constructor:
    /// <c>"%s/%s (Linux; U; Android %s; %s; %s; Build/%s)"</c> over the package name, the app
    /// version, the OS version, the <i>locale</i>, the device model and the build id.
    /// </para>
    /// <para>
    /// What was sent before was <c>com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U;
    /// Android 12; GB) gzip</c> - the right package and version wrapped in a shape no YouTube
    /// app has ever sent: a bare country where the locale goes, no device model, no build id,
    /// and a trailing <c>gzip</c> from a different convention entirely.
    /// </para>
    /// <para>
    /// The locale is <c>Locale.getDefault()</c> on a handset. Here it is fixed at
    /// <c>en_US</c>, to match the <c>hl</c> and <c>gl</c> the body carries.
    /// </para>
    /// </summary>
    /// <param name="client">The client to describe.</param>
    /// <returns>The User-Agent header value.</returns>
    private static string AndroidUserAgent(InnertubeClient client) => string.Create(
        CultureInfo.InvariantCulture,
        $"{client.PackageName}/{client.Version} (Linux; U; Android {client.OsVersion}; en_US; {client.DeviceModel}; Build/{client.BuildId})");

    /// <summary>
    /// The app player endpoint, with ReVanced's own field mask.
    /// <para>
    /// Theirs is <c>player?fields=streamingData&amp;alt=proto</c>. The mask is copied and
    /// widened by one field - <c>playabilityStatus</c>, which this resolver reads to tell a
    /// refusal from an answer and theirs does not need. <b><c>alt=proto</c> is deliberately not
    /// copied</b>: ReVanced asks for protobuf and parses it, there is no generated protobuf
    /// here, and JSON is the same answer in a shape this can read. That is the only part of the
    /// request that differs from theirs on purpose.
    /// </para>
    /// </summary>
    private const string AppPlayerEndpoint =
        "https://youtubei.googleapis.com/youtubei/v1/player"
        + "?prettyPrint=false&fields=playabilityStatus,streamingData";

    /// <summary>
    /// The reel endpoint, for the one client ReVanced does not give the player endpoint.
    /// <para>
    /// Same mask shape as theirs, nested: the whole player response arrives under
    /// <c>playerResponse</c>, which is why <see cref="PlayerResponse"/> exists.
    /// </para>
    /// </summary>
    private const string ReelPlayerEndpoint =
        "https://youtubei.googleapis.com/youtubei/v1/reel/reel_item_watch"
        + "?prettyPrint=false&fields=playerResponse.playabilityStatus,playerResponse.streamingData";

    /// <summary>
    /// The <c>context.client</c> object, in ReVanced's own field order.
    /// <para>
    /// <b>No <c>platform</c>.</b> This used to send <c>"MOBILE"</c> for every client and
    /// <c>"DESKTOP"</c> for the web one. ReVanced's <c>createInnertubeBody</c> sends no such
    /// field at all, for any client - it was invented here, and an invented field is exactly
    /// the sort of thing that makes a request not look like the app it claims to be.
    /// </para>
    /// </summary>
    /// <param name="client">The client to describe.</param>
    /// <returns>The client context.</returns>
    private static Dictionary<string, object?> Context(InnertubeClient client)
    {
        var context = new Dictionary<string, object?>
        {
            ["deviceMake"] = client.DeviceMake,
            ["deviceModel"] = client.DeviceModel,
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["osName"] = client.OsName,
            ["osVersion"] = client.OsVersion
        };

        if (client.AndroidSdkVersion is { } sdk)
        {
            context["androidSdkVersion"] = sdk.ToString(CultureInfo.InvariantCulture);
        }

        // The visitor id the television's token was minted against. It has to be the same
        // value here as it was there - a PO token is bound to it - which is why the box sends
        // both and this never invents one.
        if (ProofOfOrigin.Held is { } held)
        {
            context["visitorData"] = held.VisitorData;
        }

        context["hl"] = "en";
        context["gl"] = "US";

        return context;
    }

    private async Task<StreamCandidate?> TryInnertubeAsync(string id, InnertubeClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;

            var request_ = new Dictionary<string, object?>
            {
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true,
                ["videoId"] = id
            };

            var body = new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?>
                {
                    ["client"] = Context(client)
                }
            };

            // The player-request token, when a separate one was minted. YouTube treats the
            // player and the stream as different contexts, so the stream token is not sent
            // here as a stand-in - a token from the wrong context is worse than none.
            if (ProofOfOrigin.Held?.PlayerToken is { } playerToken)
            {
                body["serviceIntegrityDimensions"] = new Dictionary<string, object?>
                {
                    ["poToken"] = playerToken
                };
            }

            if (client.UsePlayerEndpoint)
            {
                foreach (var (key, value) in request_)
                {
                    body[key] = value;
                }
            }
            else
            {
                // ReVanced's reel shape: the request nested, and the player response asked for
                // rather than disabled.
                body["playerRequest"] = request_;
                body["disablePlayerResponse"] = false;
            }

            // The app endpoint, not the website's. ReVanced's own requests go to
            // youtubei.googleapis.com, which is where an application client belongs; and an
            // application client does not send a browser's Origin and Referer, so neither does
            // this one unless it is pretending to be a browser. Sending a web page's headers
            // with an Oculus user agent is not a client YouTube has ever seen.
            var browser = client.Browser;

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                browser
                    ? "https://www.youtube.com/youtubei/v1/player?prettyPrint=false"
                    : client.UsePlayerEndpoint ? AppPlayerEndpoint : ReelPlayerEndpoint)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);

            if (browser)
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", Referer);
            }

            // The number, not the name - and ReVanced says so out loud where they set it:
            // "Not a typo. \"Client-Name\" uses the client type id." The Morphe fork sends the
            // string here, which is one of the two things reading a fork instead of the real
            // repository got wrong.
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", client.NameId);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.Version);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken)
                .ConfigureAwait(false);

            var root = PlayerResponse(json.RootElement);
            if (root.TryGetProperty("playabilityStatus", out var playability)
                && playability.TryGetProperty("status", out var status)
                && status.GetString() is { } text
                && !string.Equals(text, "OK", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("streamingData", out var streaming))
            {
                return null;
            }

            // A manifest is preferred over a single file: it carries the quality ladder, so
            // the player picks rather than being handed whatever one guess seemed best.
            foreach (var manifest in new[] { "hlsManifestUrl", "dashManifestUrl" })
            {
                if (streaming.TryGetProperty(manifest, out var url) && url.GetString() is { Length: > 0 } value)
                {
                    return new StreamCandidate(value, Manifest, client.Name);
                }
            }

            // Adaptive first, muxed as the floor. The muxed list holds itag 18 and little
            // else - 360p - so everything worth watching is in adaptiveFormats, as a video
            // stream and an audio stream that have to be played together.
            //
            // Those streams come with two conditions, both measured on 21 Aug 2026 and both
            // the caller's to keep. **Read them in windows**: a request for the whole file, or
            // for tens of megabytes, is answered 403 while a megabyte at any offset is fine.
            // And **use them soon**: a URL minutes old starts refusing everything past its
            // first window, which is why nothing here is cached for long any more.
            var adaptive = streaming.TryGetProperty("adaptiveFormats", out var adaptiveFormats)
                ? BestPair(adaptiveFormats, client.Name)
                : null;
            var muxed = streaming.TryGetProperty("formats", out var formats)
                ? BestOf(formats, client.Name)
                : null;

            if (adaptive is null || (muxed is not null && muxed.Quality > adaptive.Quality))
            {
                return muxed ?? adaptive;
            }

            // And check that the good one can actually be played to the end before preferring
            // it. See ServesPastTheOpeningAsync: the conditions on these streams move, and on
            // 21 Aug 2026 they moved again to serving only the opening stretch of a file.
            if (!await ServesPastTheOpeningAsync(adaptive, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "LiteTV: {Quality}p adaptive is capped today, falling back to {Fallback}",
                    adaptive.Quality,
                    muxed is null ? "nothing better" : Describe(muxed));

                // Ranked at the floor when this client has no muxed format to offer, so the
                // ladder keeps looking and any other client's 360p wins - a capped stream is
                // worth less than the worst thing that plays through. It is still returned
                // rather than dropped, because a minute of a trailer beats silence when there
                // is nothing else at all.
                return muxed ?? adaptive with { Quality = Capped };
            }

            return adaptive;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Every rung is allowed to fail; that is what the next one is for.
            return null;
        }
    }

    private async Task<StreamCandidate?> TryMirrorAsync(
        string url,
        string? directProperty,
        string streamsProperty,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken)
                .ConfigureAwait(false);

            var root = json.RootElement;
            if (directProperty is not null
                && root.TryGetProperty(directProperty, out var direct)
                && direct.ValueKind == JsonValueKind.String
                && direct.GetString() is { Length: > 0 } hls)
            {
                return new StreamCandidate(hls, Manifest, source);
            }

            return root.TryGetProperty(streamsProperty, out var streams) ? BestOf(streams, source) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks a video stream and an audio stream to be played together.
    /// <para>
    /// This is where the quality is: YouTube keeps H.264 at 1080p, and AAC at 128 kbps, in the
    /// adaptive lists only. The muxed list a single-URL player wants is 360p.
    /// </para>
    /// <para>
    /// H.264 in MP4 is preferred and nothing above 1080p is taken: a television decodes that
    /// combination in hardware. The preference is deliberately small - a hundred points against
    /// a height in the hundreds - so it decides between two streams of the same size and never
    /// lets 360p H.264 beat 720p VP9. AV1 is skipped outright: hardware support for it is the
    /// one thing a set-top box of unknown vintage most often lacks.
    /// </para>
    /// <para>
    /// Audio is AAC, preferring stereo - a 5.1 track would be remixed by whatever the set is
    /// plugged into, which for a thirty-second trailer is a poor trade.
    /// </para>
    /// </summary>
    private static StreamCandidate? BestPair(JsonElement streams, string source)
    {
        if (streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? video = null;
        var videoHeight = 0;
        var videoScore = int.MinValue;
        string? audio = null;
        var audioScore = int.MinValue;

        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("url", out var urlElement) || urlElement.GetString() is not { Length: > 0 } url)
            {
                // A stream whose URL has to be un-ciphered by running YouTube's own JavaScript
                // is not worth the machinery; the clients we claim to be hand back plain URLs.
                continue;
            }

            var mime = stream.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;

            if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                var height = Height(stream);
                if (height is 0 or > 1080 || mime.Contains("av01", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = height;
                if (mime.Contains("avc1", StringComparison.OrdinalIgnoreCase)) score += 100;
                if (mime.Contains("mp4", StringComparison.OrdinalIgnoreCase)) score += 30;
                if (score > videoScore)
                {
                    videoScore = score;
                    videoHeight = height;
                    video = url;
                }
            }
            else if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                var score = 0;
                if (mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) score += 4000;
                var channels = stream.TryGetProperty("audioChannels", out var c) && c.TryGetInt32(out var count)
                    ? count
                    : 2;
                if (channels <= 2) score += 2000;
                if (stream.TryGetProperty("bitrate", out var b) && b.TryGetInt32(out var bitrate))
                {
                    score += Math.Min(bitrate / 1000, 320);
                }

                if (score > audioScore)
                {
                    audioScore = score;
                    audio = url;
                }
            }
        }

        return video is null || audio is null ? null : new StreamCandidate(video, videoHeight, source, audio);
    }

    /// <summary>
    /// Picks a stream that carries its own audio. A video-only track is the commonest thing
    /// on offer and the one thing that must not be chosen: it plays in silence.
    /// <para>
    /// The candidate carries the height it settled on, because the caller has to be able to
    /// tell "the best this rung had" from "good enough to stop looking".
    /// </para>
    /// </summary>
    private static StreamCandidate? BestOf(JsonElement streams, string source)
    {
        if (streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var bestHeight = 0;
        var bestScore = int.MinValue;

        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("url", out var urlElement) || urlElement.GetString() is not { Length: > 0 } url)
            {
                continue;
            }

            var mime = stream.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var videoOnly = stream.TryGetProperty("videoOnly", out var vo) && vo.ValueKind == JsonValueKind.True;
            var hasAudio = !videoOnly
                && (mime.Length == 0
                    || mime.Contains("audio", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase)
                    || stream.TryGetProperty("audioQuality", out _));

            if (!hasAudio)
            {
                continue;
            }

            var score = 0;

            // H.264 in MP4 first: it is the one combination every television decodes in
            // hardware. A trailer that drops frames is worse than a lower resolution.
            if (mime.Contains("avc1", StringComparison.OrdinalIgnoreCase)) score += 400;
            if (mime.Contains("mp4", StringComparison.OrdinalIgnoreCase)) score += 200;
            var height = Height(stream);
            score += height;

            if (score > bestScore)
            {
                bestScore = score;
                bestHeight = height;
                best = url;
            }
        }

        return best is null ? null : new StreamCandidate(best, bestHeight, source);
    }

    private static int Height(JsonElement stream)
    {
        if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var height))
        {
            return Math.Min(height, 1080);
        }

        // The mirrors give a label rather than a number: "1080p60", "720p".
        foreach (var name in new[] { "quality", "qualityLabel", "resolution" })
        {
            if (!stream.TryGetProperty(name, out var q) || q.GetString() is not { } label)
            {
                continue;
            }

            var digits = new string(label.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return Math.Min(parsed, 1080);
            }
        }

        return 0;
    }

    /// <summary>
    /// One answer from one rung of the ladder, with how good it is.
    /// <para>
    /// <see cref="Quality"/> is a picture height, and a manifest counts as
    /// <see cref="Manifest"/> - better than any single file, because the player picks off the
    /// whole ladder rather than being handed one guess.
    /// </para>
    /// </summary>
    private sealed record StreamCandidate(string Url, int Quality, string Source, string? AudioUrl = null)
    {
        /// <summary>
        /// Gets the picture address, carrying the television's token when one is held.
        /// <para>
        /// Signed <b>here</b>, at the moment the address comes out of the answer, rather than
        /// on the way out to the caller. Everything downstream then works on the address that
        /// will actually be played - and one of those things is
        /// <see cref="ServesPastTheOpeningAsync"/>, which would otherwise test an unsigned URL,
        /// find the sixty-second wall, and report a stream as capped when the token had already
        /// lifted it.
        /// </para>
        /// </summary>
        public string Url { get; init; } = ProofOfOrigin.Sign(Url);

        /// <summary>Gets the audio address, signed the same way.</summary>
        public string? AudioUrl { get; init; } = AudioUrl is null ? null : ProofOfOrigin.Sign(AudioUrl);

        /// <summary>Gets what the caller plays: the addresses, without the ranking.</summary>
        public ResolvedStream Stream => new(Url, AudioUrl, Source, Quality);
    }

    /// <summary>
    /// Whether an adaptive stream will be served past its opening, which is not a given.
    /// <para>
    /// The conditions on these URLs have moved three times in two days. They were whole-file;
    /// then windows of a megabyte at any offset; and on 21 Aug 2026, measured against
    /// googlevideo, **only the opening of a file is served at all** - a hundred kilobytes at
    /// 20% of the way in came back 206 and the same request at 46% came back 403, on a URL
    /// seconds old, and it made no difference whether the range was asked for in a header or
    /// as a query parameter. For a 128 kbps audio stream that is about a minute of sound,
    /// which is exactly how it was reported: the trailer stops just past a minute.
    /// </para>
    /// <para>
    /// So the pick is checked rather than assumed. One small range beyond the opening, on the
    /// audio stream when there is one - it is the smaller file and the one that runs out
    /// first. A refusal means the good rendition is unplayable today whatever the ladder says
    /// about it, and 360p muxed that plays through is worth more than 1080p that stops.
    /// </para>
    /// <para>
    /// It costs one request per resolve, and only when an adaptive pair has won. When YouTube
    /// stops capping, this quietly starts preferring the good renditions again with nothing to
    /// change.
    /// </para>
    /// </summary>
    private async Task<bool> ServesPastTheOpeningAsync(StreamCandidate candidate, CancellationToken cancellationToken)
    {
        var target = candidate.AudioUrl ?? candidate.Url;
        var length = ContentLength(target);
        if (length <= 0)
        {
            // Nothing to aim at. Assume it plays rather than dropping to 360p on a guess.
            return true;
        }

        // Near the end, not merely past the opening. Two thirds was not enough: an iOS 1080p
        // pair measured on 21 Aug served its audio at half way and refused it at nine tenths,
        // so the probe passed and the trailer would still have stopped - later than a minute
        // in, and just as wrong. Whatever serves 95% serves the lot.
        var from = (long)(length * 0.95);

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(6);

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", Referer);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, from + 1024);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A network hiccup is not evidence of a cap, and dropping every trailer to 360p
            // because one probe timed out would be the worse mistake.
            _logger.LogDebug(ex, "LiteTV: could not check whether the adaptive stream is capped");
            return true;
        }
    }

    /// <summary>The file's length, which googlevideo puts in the URL as <c>clen</c>.</summary>
    private static long ContentLength(string url)
    {
        var at = url.IndexOf("clen=", StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        var value = url.AsSpan(at + 5);
        var end = value.IndexOf('&');
        return long.TryParse(end < 0 ? value : value[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : 0;
    }

    /// <summary>
    /// The rank of a stream that will not play past its opening: below everything, so any
    /// other client's muxed format beats it, and above nothing, so it is still there if the
    /// whole ladder comes back capped.
    /// </summary>
    private const int Capped = 1;

    /// <summary>For the log, so a bad-looking trailer can be explained without guessing.</summary>
    private static string Describe(StreamCandidate candidate) =>
        candidate.Quality == Manifest
            ? "a manifest"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{candidate.Quality}p{(candidate.AudioUrl is null ? " muxed" : " with separate audio")}");

    private sealed record InnertubeClient(string Name, string NameId, string Version)
    {
        /// <summary>The app's package name, for the Android clients that generate a User-Agent.</summary>
        public string? PackageName { get; init; }

        /// <summary>Who made the device this client claims to run on - Oculus, Google, Apple.</summary>
        public string? DeviceMake { get; init; }

        /// <summary>The device this client claims to be running on.</summary>
        public string? DeviceModel { get; init; }

        /// <summary>The operating system's name, as the client reports it.</summary>
        public string? OsName { get; init; }

        /// <summary>The operating system's version, as the client reports it.</summary>
        public string? OsVersion { get; init; }

        /// <summary>The Android API level, for the Android clients that send one.</summary>
        public int? AndroidSdkVersion { get; init; }

        /// <summary>The Android build id, which the generated User-Agent ends with.</summary>
        public string? BuildId { get; init; }

        /// <summary>
        /// A User-Agent given outright, for the clients that are not Android and therefore have
        /// none to generate.
        /// </summary>
        public string? UserAgentOverride { get; init; }

        /// <summary>
        /// Whether this client asks the player endpoint. ReVanced's ANDROID_REEL is the one that
        /// does not: it asks <c>reel/reel_item_watch</c>, with the request nested under
        /// <c>playerRequest</c> and the answer nested under <c>playerResponse</c>.
        /// </summary>
        public bool UsePlayerEndpoint { get; init; } = true;

        /// <summary>Whether this client talks to the website rather than the app endpoint.</summary>
        public bool Browser { get; init; }

        /// <summary>
        /// The User-Agent to send: the given one, or ReVanced's generated Android shape.
        /// </summary>
        public string UserAgent => UserAgentOverride ?? AndroidUserAgent(this);
    }

    private sealed record CachedStream(ResolvedStream Stream, DateTime ExpiresUtc);
}
