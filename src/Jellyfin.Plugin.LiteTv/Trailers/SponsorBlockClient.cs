using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Asks SponsorBlock which parts of a trailer are not the trailer.
/// <para>
/// German trailers mostly come from channels that wrap them: a branded card at the front, a
/// "subscribe" plea at the back, sometimes a read for something unrelated in between. On a
/// television that is not a minor annoyance - the break is a minute long and a quarter of it
/// can be somebody else's advertising, inside what is already an advert.
/// </para>
/// <para>
/// A trailer's segments are <b>skipped outright and silently</b>, never offered as a button.
/// The fork does offer a skip button for library content, driven by Jellyfin's own media
/// segments and the viewer's per-type preferences, and that is right for a programme. A
/// sixty-second interstitial is not something anyone should have to press a button in.
/// </para>
/// </summary>
public sealed class SponsorBlockClient
{
    /// <summary>
    /// The categories worth removing from a trailer.
    /// <para>
    /// <c>intro</c> and <c>outro</c> earn their place here in a way they never would inside a
    /// film: on a trailer upload they are the *uploader's* branded top and tail, not the
    /// film's. <c>preview</c> and <c>poi_highlight</c> are deliberately absent - a preview of
    /// what is coming is the entire point of the thing being played.
    /// </para>
    /// </summary>
    private static readonly string[] Categories =
    {
        "sponsor", "selfpromo", "interaction", "intro", "outro", "music_offtopic", "filler"
    };

    /// <summary>
    /// Segments change far more slowly than stream URLs expire, and a trailer that comes round
    /// again on the same channel should not cost another request.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SponsorBlockClient> _logger;
    private readonly ConcurrentDictionary<string, CachedSegments> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SponsorBlockClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SponsorBlockClient(IHttpClientFactory httpClientFactory, ILogger<SponsorBlockClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the parts of a video that should be skipped over.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, in order; empty when there are none or the lookup failed.</returns>
    public async Task<IReadOnlyList<Segment>> SegmentsAsync(string? videoId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(videoId))
        {
            return Array.Empty<Segment>();
        }

        if (_cache.TryGetValue(videoId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Segments;
        }

        var segments = await FetchAsync(videoId, cancellationToken).ConfigureAwait(false);
        _cache[videoId] = new CachedSegments(segments, DateTime.UtcNow.Add(CacheFor));
        return segments;
    }

    /// <summary>
    /// Gets segments already fetched, without going anywhere near the network.
    /// <para>
    /// For the guide, which is walked synchronously while a request waits on it and must never
    /// be the thing that blocks on somebody else's service. A break sized without the segments
    /// is the behaviour this plugin had all along; a break sized with them is better whenever
    /// the answer happens to be at hand, which it is for any trailer that has aired recently.
    /// </para>
    /// </summary>
    /// <param name="videoId">The video.</param>
    /// <returns>The segments, or null when none have been fetched or the answer has aged out.</returns>
    public IReadOnlyList<Segment>? SegmentsIfCached(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId))
        {
            return null;
        }

        return _cache.TryGetValue(videoId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow
            ? cached.Segments
            : null;
    }

    private async Task<IReadOnlyList<Segment>> FetchAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            // The hash-prefix form of the API: SponsorBlock is asked about every video whose id
            // begins with these four hex characters, and which one we actually wanted is worked
            // out here. It costs a slightly larger answer and means the server is never told
            // what is being watched - which is the same principle the whole plugin exists for,
            // applied to somebody else's service.
            var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))
                .ToLowerInvariant()[..4];
            var categories = Uri.EscapeDataString("[\"" + string.Join("\",\"", Categories) + "\"]");

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Jellyfin.Plugin.LiteTv");

            using var response = await http
                .GetAsync($"https://sponsor.ajay.app/api/skipSegments/{prefix}?categories={categories}", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 is the ordinary answer for "nobody has marked anything".
                return Array.Empty<Segment>();
            }

            using var json = await JsonDocument
                .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), default, cancellationToken)
                .ConfigureAwait(false);

            var found = new List<Segment>();
            foreach (var video in json.RootElement.EnumerateArray())
            {
                if (!video.TryGetProperty("videoID", out var id)
                    || !string.Equals(id.GetString(), videoId, StringComparison.Ordinal)
                    || !video.TryGetProperty("segments", out var segments))
                {
                    continue;
                }

                foreach (var segment in segments.EnumerateArray())
                {
                    var parsed = Parse(segment);
                    if (parsed is not null)
                    {
                        found.Add(parsed);
                    }
                }
            }

            // Overlaps would make the player seek backwards into a segment it just left.
            found.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            var merged = Merge(found);

            if (merged.Count > 0)
            {
                _logger.LogDebug(
                    "LiteTV: SponsorBlock has {Count} segment(s) for trailer {Id}: {Categories}",
                    merged.Count,
                    videoId,
                    string.Join(", ", merged.Select(s => s.Category)));
            }

            return merged;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            // A trailer with an unskipped sponsor read is a far smaller problem than a channel
            // that stalls because a third-party service is down.
            return Array.Empty<Segment>();
        }
    }

    private static Segment? Parse(JsonElement segment)
    {
        // "skip" is the only action worth acting on here. "mute" leaves the picture running,
        // "poi" is a single point rather than a range, and "full" marks the whole video as one
        // category - which for a trailer would mean skipping the trailer.
        if (segment.TryGetProperty("actionType", out var action)
            && action.GetString() is { } actionType
            && !string.Equals(actionType, "skip", StringComparison.Ordinal))
        {
            return null;
        }

        // Votes below zero mean the community has been arguing about it; on a thirty-second
        // clip a wrong skip is most of the clip.
        if (segment.TryGetProperty("votes", out var votes) && votes.TryGetInt32(out var count) && count < 0)
        {
            return null;
        }

        if (!segment.TryGetProperty("segment", out var range)
            || range.ValueKind != JsonValueKind.Array
            || range.GetArrayLength() != 2)
        {
            return null;
        }

        var start = range[0].GetDouble();
        var end = range[1].GetDouble();
        if (end <= start)
        {
            return null;
        }

        var category = segment.TryGetProperty("category", out var c) ? c.GetString() ?? "unknown" : "unknown";
        return new Segment(start, end, category);
    }

    private static List<Segment> Merge(List<Segment> sorted)
    {
        var merged = new List<Segment>();
        foreach (var segment in sorted)
        {
            if (merged.Count > 0 && segment.StartSeconds <= merged[^1].EndSeconds)
            {
                var last = merged[^1];
                merged[^1] = last with { EndSeconds = Math.Max(last.EndSeconds, segment.EndSeconds) };
                continue;
            }

            merged.Add(segment);
        }

        return merged;
    }

    /// <summary>
    /// A stretch of a trailer that is not the trailer.
    /// </summary>
    /// <param name="StartSeconds">Where it starts.</param>
    /// <param name="EndSeconds">Where it ends, and where the player should carry on from.</param>
    /// <param name="Category">What SponsorBlock calls it, for the log and for the client.</param>
    public sealed record Segment(double StartSeconds, double EndSeconds, string Category)
    {
        /// <summary>Gets a short description, for logging.</summary>
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Category} {StartSeconds:F1}-{EndSeconds:F1}s");
    }

    private sealed record CachedSegments(IReadOnlyList<Segment> Segments, DateTime ExpiresUtc);
}
