using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Finds the official trailer YouTube presents for a title in a requested spoken language.
/// Jellyfin metadata commonly keeps only one remote-trailer address, even when both the German
/// and English releases exist. Interactive trailer playback must not mistake that one link for
/// the complete catalogue.
/// </summary>
public sealed class YouTubeTrailerSearch
{
    private const string SearchEndpoint =
        "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeTrailerSearch> _logger;
    private readonly ConcurrentDictionary<string, CachedResult> _cache = new(StringComparer.Ordinal);

    /// <summary>One YouTube result that can be handed to the stream resolver.</summary>
    public sealed record Result(string Name, string Url);

    private sealed record CachedResult(Result? Result, DateTime ExpiresAtUtc);

    /// <summary>Initializes a new instance of the <see cref="YouTubeTrailerSearch"/> class.</summary>
    public YouTubeTrailerSearch(
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeTrailerSearch> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Finds the first official trailer result for <paramref name="title"/> in
    /// <paramref name="language"/>. This is deliberately a narrow query: the UI needs one
    /// German and one English button, not a result list.
    /// </summary>
    public async Task<Result?> OfficialTrailerAsync(
        string? title,
        int? year,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.Join(
            ' ',
            new[] { title.Trim(), year?.ToString(), "official trailer", LanguageWords(language) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (_cache.TryGetValue(query, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Result;
        }

        Result? result = null;
        try
        {
            using var http = _httpClientFactory.CreateClient();
            var body = new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?>
                {
                    ["client"] = new Dictionary<string, object?>
                    {
                        ["clientName"] = "WEB",
                        ["clientVersion"] = "2.20240722.01.00",
                        ["hl"] = language,
                        ["gl"] = language.Equals("de", StringComparison.OrdinalIgnoreCase) ? "DE" : "US"
                    }
                },
                ["query"] = query
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.TryAddWithoutValidation("User-Agent", YouTubeStreamResolver.UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", YouTubeStreamResolver.Referer);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20240722.01.00");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var json = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    default,
                    cancellationToken).ConfigureAwait(false);
                result = Harvest(json.RootElement).FirstOrDefault();
            }
            else
            {
                _logger.LogInformation(
                    "LiteTV YouTube trailer search for {Title} ({Language}) returned {Status}.",
                    title,
                    language,
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogInformation(ex, "LiteTV could not search YouTube trailers for {Title} ({Language}).", title, language);
        }

        _cache[query] = new CachedResult(result, DateTime.UtcNow + CacheFor);
        return result;
    }

    internal static IReadOnlyList<Result> Harvest(JsonElement root)
    {
        var results = new List<Result>();
        Walk(root, results);
        return results;
    }

    private static void Walk(JsonElement element, List<Result> results)
    {
        if (results.Count >= 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("videoRenderer", out var video))
                {
                    var id = video.TryGetProperty("videoId", out var videoId) ? videoId.GetString() : null;
                    var name = Text(video, "title");
                    if (!string.IsNullOrWhiteSpace(id)
                        && !string.IsNullOrWhiteSpace(name)
                        && name.Contains("trailer", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new Result(name, "https://www.youtube.com/watch?v=" + id));
                    }

                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, results);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, results);
                }

                break;
        }
    }

    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.TryGetProperty("simpleText", out var simple))
        {
            return simple.GetString();
        }

        return value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? string.Concat(runs.EnumerateArray().Select(run => run.TryGetProperty("text", out var text) ? text.GetString() : null))
            : null;
    }

    private static string LanguageWords(string language) => language.ToLowerInvariant() switch
    {
        "de" => "deutsch",
        "ru" => "русский",
        _ => "english"
    };
}
