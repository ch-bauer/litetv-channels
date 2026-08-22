using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Integrations;

/// <summary>
/// Asks the Smart Similar plugin to score a set of seed titles. LiteTV does not
/// re-implement any of that scoring: the plugin already knows how to weigh genres,
/// people, tags and years, it caches the candidate list, and it is the owner's own.
/// This is a call over the loopback interface carrying the caller's own token, so
/// the answer is limited to the library that user can see.
/// </summary>
public class SmartSimilarClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Jellyfin serves PascalCase, its own clients hedge for camelCase; take either.
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SiblingPlugins _siblings;
    private readonly ILogger<SmartSimilarClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSimilarClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Makes the loopback request.</param>
    /// <param name="siblings">Says whether the plugin is there to ask.</param>
    /// <param name="logger">The logger.</param>
    public SmartSimilarClient(
        IHttpClientFactory httpClientFactory,
        SiblingPlugins siblings,
        ILogger<SmartSimilarClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _siblings = siblings;
        _logger = logger;
    }

    /// <summary>
    /// Scores the library against several seeds.
    /// </summary>
    /// <param name="baseUri">The server's own address, taken from the incoming request.</param>
    /// <param name="authorization">The caller's Authorization header, passed straight through.</param>
    /// <param name="seeds">The titles the suggestions are built from.</param>
    /// <param name="userId">The user whose library access applies.</param>
    /// <param name="minScore">Floor on the score, or null for the plugin's own setting.</param>
    /// <param name="limit">Maximum results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored pool, or null when the plugin is absent or did not answer.</returns>
    public async Task<SmartSimilarScore?> ScoreAsync(
        Uri baseUri,
        string? authorization,
        IReadOnlyList<Guid> seeds,
        Guid userId,
        int? minScore,
        int limit,
        CancellationToken cancellationToken)
    {
        if (seeds.Count == 0)
        {
            return null;
        }

        // Asked by GUID before anything is sent: a plugin that is not installed
        // should cost nothing, and a 404 could not tell us why it failed anyway.
        if (!_siblings.IsUsable(SiblingPlugins.SmartSimilarId))
        {
            return null;
        }

        var query = "SmartSimilar/Score"
            + "?itemIds=" + string.Join(',', seeds.Select(id => id.ToString("N")))
            + "&userId=" + userId.ToString("N")
            + "&limit=" + limit
            + (minScore.HasValue ? "&minScore=" + minScore.Value : string.Empty);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, query));
            if (!string.IsNullOrEmpty(authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Smart Similar answered {Status} for {Count} seeds; falling back.",
                    (int)response.StatusCode,
                    seeds.Count);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<SmartSimilarScore>(stream, SerializerOptions, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Suggestions are a convenience: never fail the page over them.
            _logger.LogWarning(ex, "Could not reach Smart Similar; falling back to the rough scorer.");
            return null;
        }
    }
}

/// <summary>Smart Similar's answer to a scoring request.</summary>
public class SmartSimilarScore
{
    /// <summary>Gets or sets a value indicating whether any seed could be scored.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets the seeds as the plugin understood them.</summary>
    public IReadOnlyList<SmartSimilarSeed> Seeds { get; set; } = Array.Empty<SmartSimilarSeed>();

    /// <summary>Gets or sets the ranked results.</summary>
    public IReadOnlyList<SmartSimilarResult> Results { get; set; } = Array.Empty<SmartSimilarResult>();
}

/// <summary>One seed, and what actually answered for it.</summary>
public class SmartSimilarSeed
{
    /// <summary>Gets or sets the seed's item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets "Movie" or "Series".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the seed's type is handled.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets which engine answered: Local, Tmdb or Hybrid.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>One scored candidate.</summary>
public class SmartSimilarResult
{
    /// <summary>Gets or sets the candidate's item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets "Movie" or "Series".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the mean score over the comparable seeds, 0-100.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets the score per seed, aligned with the request order.</summary>
    public IReadOnlyList<double?> PerSeed { get; set; } = Array.Empty<double?>();

    /// <summary>Gets or sets what this candidate has in common with the seeds.</summary>
    [JsonPropertyName("Shared")]
    public SmartSimilarShared? Shared { get; set; }
}

/// <summary>The metadata behind a score - what a screen shows to explain a suggestion.</summary>
public class SmartSimilarShared
{
    /// <summary>Gets or sets the shared genres.</summary>
    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the shared tags.</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the shared directors, writers and actors.</summary>
    public IReadOnlyList<string> People { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the shared studios.</summary>
    public IReadOnlyList<string> Studios { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the closest gap in years to any seed.</summary>
    public int? YearGap { get; set; }

    /// <summary>Gets or sets a value indicating whether the age rating matches a seed's.</summary>
    public bool OfficialRating { get; set; }
}
