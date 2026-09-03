using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// Turns the library into ready-to-air channel ideas. It describes a programme identity rather
/// than borrowing a broadcaster's name: the suggestion is about the owner's media, not an
/// assertion that it is an official channel.
/// </summary>
internal static class ChannelSuggestionBuilder
{
    private const int MaximumSources = 12;

    /// <summary>Builds the useful, distinct channel templates the current library can support.</summary>
    internal static List<ChannelSuggestionDto> Build(
        IEnumerable<Series> series,
        IEnumerable<Movie> movies,
        IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var all = series.Cast<BaseItem>().Concat(movies).ToList();
        var result = new List<ChannelSuggestionDto>();

        AddIfUseful(result, existing, "Werkstatt & Wildnis",
            "Raues Faktenfernsehen aus Dokus, Abenteuer- und Reality-Titeln deiner Bibliothek.",
            "Fakten & Abenteuer",
            FilterByGenre(all, "Documentary", "Dokumentation", "Reality", "Adventure", "Abenteuer", "Action"),
            movieNight: true);

        AddIfUseful(result, existing, "Disney & Pixar",
            "Familienfilme und Serien, deren Studio-Metadaten Disney, Pixar, Marvel oder Lucasfilm nennen.",
            "Familie & Animation",
            all.Where(IsDisneyFamily),
            movieNight: true);

        AddIfUseful(result, existing, "Kinderzeit",
            "Altersgerechte Serien und Filme aus deiner Bibliothek, als abwechslungsreiches Tagesprogramm.",
            "Kinderprogramm",
            all.Where(IsKids),
            movieNight: true);

        foreach (var genre in ByGenre(all)
            .Where(pair => pair.Value.Count >= 4)
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(6))
        {
            var profile = GenreProfile(genre.Key);
            AddIfUseful(result, existing, profile.Name,
                genre.Value.Count + " lokale Titel mit " + genre.Key + ".",
                profile.Theme,
                genre.Value,
                movieNight: true);
        }

        // Leave room for collection marathons, which are added by the controller because only it
        // has to resolve a BoxSet's linked children.
        return result.Take(6).ToList();
    }

    private static void AddIfUseful(
        List<ChannelSuggestionDto> result,
        HashSet<string> existing,
        string name,
        string description,
        string theme,
        IEnumerable<BaseItem> pool,
        bool movieNight)
    {
        if (existing.Contains(name) || result.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var titles = pool
            .Where(item => item.Id != Guid.Empty)
            .DistinctBy(item => item.Id)
            .OrderByDescending(item => item is Series)
            .ThenByDescending(item => item.CommunityRating ?? 0)
            .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSources)
            .ToList();
        if (titles.Count < 3)
        {
            return;
        }

        var films = pool.OfType<Movie>()
            .Where(movie => movie.Id != Guid.Empty && (movie.RunTimeTicks ?? 0) > 0)
            .OrderByDescending(movie => movie.CommunityRating ?? 0)
            .ThenBy(movie => movie.SortName, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSources)
            .Cast<BaseItem>()
            .ToList();

        var suggestion = new ChannelSuggestionDto
        {
            Name = name,
            Description = description,
            Theme = theme,
            Sources = Sources(titles),
            EpisodesPerBlock = 1,
            Order = nameof(PlayOrder.WeightedShuffle),
            RandomizeEpisodes = titles.Any(item => item is Series),
            Trailers = nameof(TrailerMode.Preview),
            TrailerEveryPrograms = 3,
            TrailerLookahead = 3,
            TrailersInGaps = true,
            Artwork = new SuggestedArtworkDto { ItemId = titles[0].Id, ItemName = titles[0].Name ?? string.Empty }
        };
        suggestion.Features.Add("Gewichtet zufällig");
        if (suggestion.RandomizeEpisodes)
        {
            suggestion.Features.Add("Serienfolgen mischen");
        }

        suggestion.Features.Add("Trailer-Vorschau");
        if (movieNight && films.Count >= 3)
        {
            suggestion.MovieNight = new SuggestedProgramBlockDto
            {
                Name = "Filmabend",
                StartMinutes = 20 * 60 + 15,
                Days = new List<string> { nameof(DayOfWeek.Saturday) },
                Sources = Sources(films),
                EpisodesPerBlock = 1,
                Order = nameof(PlayOrder.WeightedShuffle),
                RandomizeEpisodes = true,
                AdvanceOnePerWeek = true,
                FitToContent = true,
                ShiftToAvoidLeadingGap = true,
                TrailerEnabled = true,
                TrailerProgramsBefore = 3
            };
            suggestion.Features.Add("Filmabend · Sa 20:15");
        }

        result.Add(suggestion);
    }

    private static List<SuggestedSourceDto> Sources(IReadOnlyList<BaseItem> items)
    {
        var baseWeight = 100 / items.Count;
        var remainder = 100 % items.Count;
        return items.Select((item, index) => new SuggestedSourceDto
        {
            Type = item is Series ? nameof(ChannelSourceType.Series) : nameof(ChannelSourceType.Movie),
            ItemId = item.Id,
            Name = item.Name ?? string.Empty,
            Probability = baseWeight + (index < remainder ? 1 : 0)
        }).ToList();
    }

    private static IEnumerable<BaseItem> FilterByGenre(IEnumerable<BaseItem> items, params string[] terms) =>
        items.Where(item => (item.Genres ?? Array.Empty<string>()).Any(genre =>
            terms.Any(term => genre.Contains(term, StringComparison.OrdinalIgnoreCase))));

    private static bool IsDisneyFamily(BaseItem item) =>
        (item.Studios ?? Array.Empty<string>()).Any(studio =>
            studio.Contains("disney", StringComparison.OrdinalIgnoreCase)
            || studio.Contains("pixar", StringComparison.OrdinalIgnoreCase)
            || studio.Contains("marvel", StringComparison.OrdinalIgnoreCase)
            || studio.Contains("lucasfilm", StringComparison.OrdinalIgnoreCase));

    private static bool IsKids(BaseItem item) =>
        item.OfficialRating is "FSK-0" or "FSK-6" or "0" or "6"
        || FilterByGenre(new[] { item }, "Animation", "Family", "Familie", "Children", "Kinder").Any();

    private static Dictionary<string, List<BaseItem>> ByGenre(IEnumerable<BaseItem> items)
    {
        var grouped = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var genre in item.Genres ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(genre))
                {
                    continue;
                }

                if (!grouped.TryGetValue(genre, out var titles))
                {
                    grouped[genre] = titles = new List<BaseItem>();
                }

                titles.Add(item);
            }
        }

        return grouped;
    }

    private static (string Name, string Theme) GenreProfile(string genre) => genre.ToLowerInvariant() switch
    {
        "action" => ("Action Arena", "Action & Abenteuer"),
        "adventure" or "abenteuer" => ("Abenteuerzeit", "Action & Abenteuer"),
        "comedy" or "komödie" => ("Comedy & Chaos", "Comedy"),
        "crime" or "krimi" => ("Krimi nach Acht", "Krimi"),
        "documentary" or "dokumentation" => ("Wissen & Welt", "Dokumentation"),
        "animation" => ("Animationswelt", "Animation"),
        "science fiction" or "sci-fi" => ("Zukunftskino", "Science-Fiction"),
        "horror" => ("Nachtkino", "Horror"),
        "drama" => ("Große Geschichten", "Drama"),
        "family" or "familie" => ("Familienkino", "Familie"),
        _ => (genre + "-TV", genre)
    };
}
