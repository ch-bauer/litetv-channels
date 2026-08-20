using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Turns a YouTube link into a URL a player can actually be handed.
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
    /// How long a resolved URL is offered again before it is looked up afresh. Google's
    /// stream URLs carry an expiry of their own, usually some hours; this stays well inside
    /// that, because a URL that dies mid-trailer is worse than a second's delay.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(45);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

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
    /// The order is measured rather than inherited. Moonfin asks ANDROID_VR first and the TV
    /// player second; on 20 Aug 2026, against four of this library's own trailers, ANDROID_VR
    /// answered <c>LOGIN_REQUIRED</c> and the TV player answered <c>ERROR</c> every time. IOS
    /// answered OK but offered only adaptive streams - video and audio apart, no use to a
    /// player handed a single URL. <b>ANDROID</b> resolved all four, to muxed H.264 with AAC,
    /// which is what a television decodes in hardware. So ANDROID leads.
    /// </para>
    /// <para>
    /// The others are kept behind it rather than deleted: they cost one fast failure each,
    /// they answer for videos ANDROID sometimes will not, and which of them works is exactly
    /// the thing that changes without notice.
    /// </para>
    /// </summary>
    private static readonly InnertubeClient[] Clients =
    {
        new("ANDROID", "3", "20.10.41", "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w", "MOBILE",
            "com.google.android.youtube/20.10.41 (Linux; U; Android 11) gzip"),
        new("IOS", "5", "20.10.4", "AIzaSyB-63vPrdThhKuerbB2N_l7Kwwcxj6yUAc", "MOBILE",
            "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)"),
        new("ANDROID_VR", "28", "1.60.19", "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w", "MOBILE",
            "com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip"),
        new("TVHTML5_SIMPLY_EMBEDDED_PLAYER", "85", "2.0", "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8", "TV",
            "Mozilla/5.0 (SMART-TV; LINUX; Tizen 6.0) AppleWebKit/538.1 (KHTML, like Gecko) Version/6.0 TV Safari/538.1"),
        new("WEB", "1", "2.20250312.04.00", "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8", "DESKTOP", UserAgent)
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
    /// <returns>A playable URL, or null when it could not be resolved. A link that is not
    /// YouTube is handed back untouched, since it may already be playable.</returns>
    public async Task<string?> ResolveAsync(string? url, CancellationToken cancellationToken)
    {
        var id = VideoId(url);
        if (id is null)
        {
            return url;
        }

        if (_cache.TryGetValue(id, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Url;
        }

        var resolved = await ResolveIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            _cache[id] = new CachedStream(resolved, DateTime.UtcNow.Add(CacheFor));
        }

        return resolved;
    }

    private async Task<string?> ResolveIdAsync(string id, CancellationToken cancellationToken)
    {
        foreach (var client in Clients)
        {
            var url = await TryInnertubeAsync(id, client, cancellationToken).ConfigureAwait(false);
            if (url is not null)
            {
                _logger.LogDebug("LiteTV: resolved trailer {Id} as {Client}", id, client.Name);
                return url;
            }
        }

        foreach (var instance in PipedInstances)
        {
            var url = await TryMirrorAsync($"{instance}/streams/{id}", "hls", "videoStreams", cancellationToken)
                .ConfigureAwait(false);
            if (url is not null)
            {
                _logger.LogDebug("LiteTV: resolved trailer {Id} via {Instance}", id, instance);
                return url;
            }
        }

        foreach (var instance in InvidiousInstances)
        {
            var url = await TryMirrorAsync($"{instance}/api/v1/videos/{id}", null, "formatStreams", cancellationToken)
                .ConfigureAwait(false);
            if (url is not null)
            {
                _logger.LogDebug("LiteTV: resolved trailer {Id} via {Instance}", id, instance);
                return url;
            }
        }

        _logger.LogInformation("LiteTV: no source could resolve trailer {Id}", id);
        return null;
    }

    private async Task<string?> TryInnertubeAsync(string id, InnertubeClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;

            var body = new Dictionary<string, object?>
            {
                ["videoId"] = id,
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true,
                ["context"] = new Dictionary<string, object?>
                {
                    ["client"] = new Dictionary<string, object?>
                    {
                        ["clientName"] = client.Name,
                        ["clientVersion"] = client.Version,
                        ["hl"] = "en",
                        ["gl"] = "US",
                        ["platform"] = client.Platform
                    }
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://www.youtube.com/youtubei/v1/player?key={client.ApiKey}&prettyPrint=false")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", Referer);
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

            var root = json.RootElement;
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
                    return value;
                }
            }

            return streaming.TryGetProperty("formats", out var formats) ? BestOf(formats) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Every rung is allowed to fail; that is what the next one is for.
            return null;
        }
    }

    private async Task<string?> TryMirrorAsync(
        string url,
        string? directProperty,
        string streamsProperty,
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
                return hls;
            }

            return root.TryGetProperty(streamsProperty, out var streams) ? BestOf(streams) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks a stream that carries its own audio. A video-only track is the commonest thing
    /// on offer and the one thing that must not be chosen: it plays in silence.
    /// </summary>
    private static string? BestOf(JsonElement streams)
    {
        if (streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
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
            score += Height(stream);

            if (score > bestScore)
            {
                bestScore = score;
                best = url;
            }
        }

        return best;
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

    private sealed record InnertubeClient(
        string Name,
        string NameId,
        string Version,
        string ApiKey,
        string Platform,
        string UserAgent);

    private sealed record CachedStream(string Url, DateTime ExpiresUtc);
}
