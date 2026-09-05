namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// Chooses the best of the links metadata providers attach to one title.
/// A RemoteTrailer has only a title and an address: its title is therefore the only safe hint
/// about its spoken language. The actual stream quality is only known after resolving it.
/// </summary>
internal static class TrailerSelection
{
    /// <summary>
    /// Gives the language preference for a linked trailer. Zero means an explicit match for the
    /// configured YouTube language, one means no language could be inferred, and two means an
    /// explicit different language. Unknown is deliberately better than a known mismatch: most
    /// providers call an English trailer simply "Official Trailer", without saying "English".
    /// </summary>
    internal static int LanguageRank(string? name, string language)
    {
        var text = name ?? string.Empty;
        var wanted = MarkersFor(language);
        if (wanted.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        return AllMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
    }

    /// <summary>Ranks the full trailer above a teaser when language and stream quality tie.</summary>
    internal static int KindRank(string? name)
    {
        var text = name ?? string.Empty;
        if (text.Contains("Official Trailer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Official Theatrical Trailer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Offizieller Trailer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Kinotrailer", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (text.Contains("Teaser", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        return text.Contains("Trailer", StringComparison.OrdinalIgnoreCase) ? 1 : 5;
    }

    private static readonly string[] AllMarkers =
    {
        "deutsch", "german", "offizieller", "kinotrailer",
        "english", "anglais", "inglés", "original version",
        "français", "french", "bande-annonce",
        "español", "spanish", "tráiler", "trailer latino",
        "italiano", "italian", "trailer ufficiale"
    };

    private static IEnumerable<string> MarkersFor(string language) => language.ToLowerInvariant() switch
    {
        "de" => new[] { "deutsch", "german", "offizieller", "kinotrailer" },
        "en" => new[] { "english", "original version" },
        "fr" => new[] { "français", "french", "bande-annonce" },
        "es" => new[] { "español", "spanish", "tráiler", "trailer latino" },
        "it" => new[] { "italiano", "italian", "trailer ufficiale" },
        _ => Array.Empty<string>()
    };
}
