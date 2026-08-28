using System.Globalization;
using Jellyfin.Plugin.LiteTv.Configuration;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Which language YouTube is asked to answer in.
/// <para>
/// YouTube localises a video's title: an uploader can give the same video a German title and an
/// English one, and the API hands back whichever the <c>hl</c> of the request asks for, falling
/// back to the original where there is no translation. So <c>hl</c> IS "German first" - it is
/// not a filter, and nothing is lost by asking in German for a video that only has an English
/// title.
/// </para>
/// <para>
/// Every call used to say <c>hl=en, gl=US</c>, hard-coded in two places, which is why a German
/// household's schedule was full of English titles for videos that had German ones. Replacing
/// that with a hard-coded <c>de</c> would be the same mistake pointing the other way, so it is
/// a setting - and the default is taken from the language the plugin is already being read in
/// rather than asking anybody to configure the same fact twice.
/// </para>
/// </summary>
public static class YouTubeLocale
{
    /// <summary>The language and region a request carries when nothing else is known.</summary>
    private const string FallbackLanguage = "en";
    private const string FallbackRegion = "US";

    /// <summary>
    /// Gets the language tag to ask YouTube in, as <c>hl</c>.
    /// </summary>
    /// <returns>A language tag such as <c>de</c> or <c>en</c>.</returns>
    public static string Language() => Split().Language;

    /// <summary>
    /// Gets the region to ask YouTube in, as <c>gl</c>.
    /// </summary>
    /// <returns>A region such as <c>DE</c> or <c>US</c>.</returns>
    public static string Region() => Split().Region;

    /// <summary>
    /// Works out the language and region from the configuration, in this order:
    /// <list type="number">
    /// <item><description><see cref="PluginConfiguration.YouTubeLanguage"/>, when it has been
    /// set - the wide control, which takes anything YouTube takes.</description></item>
    /// <item><description>the language the configuration page is read in, when that names one -
    /// somebody reading the plugin in German wants German titles, and should not have to say so
    /// twice.</description></item>
    /// <item><description>the server's own culture.</description></item>
    /// <item><description><c>en-US</c>.</description></item>
    /// </list>
    /// </summary>
    /// <returns>The language and region.</returns>
    private static (string Language, string Region) Split()
    {
        var configuration = Plugin.Instance?.Configuration;
        return From(
            configuration?.YouTubeLanguage,
            configuration?.PageLanguage,
            CultureInfo.CurrentUICulture);
    }

    /// <summary>
    /// The rule itself, with everything it depends on handed in - so it can be tested without a
    /// running server, which is the only reason it is separate from <see cref="Split"/>.
    /// </summary>
    /// <param name="youTubeLanguage">What the setting says, if anything.</param>
    /// <param name="pageLanguage">The language the page is read in.</param>
    /// <param name="server">The server's own culture.</param>
    /// <returns>The language and region.</returns>
    public static (string Language, string Region) From(
        string? youTubeLanguage,
        string? pageLanguage,
        CultureInfo server)
    {
        if (!string.IsNullOrWhiteSpace(youTubeLanguage))
        {
            return Parse(youTubeLanguage!);
        }

        // "auto" and anything else that is not a language falls through; `de` and `en` are the
        // two the page offers and both are perfectly good answers here.
        if (!string.IsNullOrWhiteSpace(pageLanguage)
            && !string.Equals(pageLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Parse(pageLanguage!);
        }

        return server is not null
            && server.TwoLetterISOLanguageName is { Length: 2 } two
            && !string.Equals(two, "iv", StringComparison.OrdinalIgnoreCase)
            ? Parse(server.Name)
            : (FallbackLanguage, FallbackRegion);
    }

    /// <summary>
    /// Splits a tag such as <c>de-DE</c>, <c>de_AT</c> or <c>de</c> into the two halves YouTube
    /// wants separately. A tag with no region gets the region whose name matches the language,
    /// which is right for the cases that matter here and harmless where it is not: <c>gl</c>
    /// steers which country's catalogue answers, not what language it answers in.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The language and region.</returns>
    private static (string Language, string Region) Parse(string tag)
    {
        var cleaned = tag.Trim().Replace('_', '-');
        var parts = cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (FallbackLanguage, FallbackRegion);
        }

        var language = parts[0].ToLowerInvariant();
        var region = parts.Length > 1
            ? parts[1].ToUpperInvariant()
            : language.ToUpperInvariant();

        return (language, region);
    }
}
