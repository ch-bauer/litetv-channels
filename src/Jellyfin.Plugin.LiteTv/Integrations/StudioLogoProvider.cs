using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Integrations;

/// <summary>
/// Fetches a studio's own logo from TMDb, for a studio/franchise channel suggestion whose
/// library has no picture for that studio at all.
/// <para>
/// The library models a studio as its own item, and a studio-images provider can give it a
/// real picture - but nothing installs one by default, and most servers have never scraped a
/// studio at all. So a suggestion that wants the studio's own mark, not a still borrowed from
/// one of its films, needs somewhere else to ask. TMDb keeps a small, clean logo for every
/// production company it knows, which is exactly the picture a studio channel wants.
/// </para>
/// <para>
/// Optional like every sibling integration this plugin has: <see cref="Configuration.PluginConfiguration.TmdbApiKey"/>
/// empty means this is never called with a usable key, and the suggestion keeps borrowing
/// artwork from one of its titles - the behaviour before this existed.
/// </para>
/// </summary>
public class StudioLogoProvider
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    // TMDb's own logo CDN. w500 is plenty for a channel tile; most company logos are transparent
    // PNGs that never carry more detail than that.
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/w500";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StudioLogoProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StudioLogoProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Makes the request to TMDb.</param>
    /// <param name="logger">The logger.</param>
    public StudioLogoProvider(IHttpClientFactory httpClientFactory, ILogger<StudioLogoProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Looks up a studio's logo.
    /// </summary>
    /// <param name="apiKey">The owner's own TMDb key, or empty to skip the lookup entirely.</param>
    /// <param name="studioName">The studio name as the library's own metadata carries it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A direct image address, or null when there is no key, no match, or no logo.</returns>
    public async Task<string?> FindLogoAsync(string? apiKey, string studioName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(studioName))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = RequestTimeout;

            var url = BaseUrl + "/search/company?api_key=" + Uri.EscapeDataString(apiKey)
                + "&query=" + Uri.EscapeDataString(studioName);

            var response = await client.GetFromJsonAsync<CompanySearchResult>(url, cancellationToken)
                .ConfigureAwait(false);

            // Best match by name proximity is overkill here: TMDb already ranks its own search,
            // and the first result with a logo at all is the one worth asking for.
            var company = response?.Results?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.LogoPath));
            return company is null ? null : ImageBaseUrl + company.LogoPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            /*
                A studio logo is decoration, not something a suggestion should fail over - the
                borrowed-title artwork already in place is a perfectly good fallback, and that is
                why this is caught here rather than left to bubble up. But caught silently is not
                the same as caught quietly: logged at Debug, this never appeared in a server's own
                log at Jellyfin's default level, so a key that was simply wrong - most commonly a
                401 from TMDb - looked identical to the key never having been set at all. Warning
                is loud enough to actually be seen without being an error the plugin did not
                cause.
            */
            var status = (ex as HttpRequestException)?.StatusCode;
            _logger.LogWarning(
                ex,
                "Could not fetch a TMDb logo for studio {Studio}{Status}",
                studioName,
                status is null ? string.Empty : " (HTTP " + (int)status.Value + ")");
            return null;
        }
    }

    private sealed class CompanySearchResult
    {
        [JsonPropertyName("results")]
        public List<Company>? Results { get; set; }
    }

    private sealed class Company
    {
        [JsonPropertyName("logo_path")]
        public string? LogoPath { get; set; }
    }
}
