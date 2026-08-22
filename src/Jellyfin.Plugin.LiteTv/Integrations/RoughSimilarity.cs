namespace Jellyfin.Plugin.LiteTv.Integrations;

/// <summary>
/// The fallback for when Smart Similar is not installed: shared genres, then decade
/// proximity, then how close the community ratings are. Deliberately dumb - no people,
/// no tags, no studios, no caches. It exists so a suggestions screen still works
/// without the other plugin, not to compete with it, and the screen says plainly which
/// of the two answered.
/// </summary>
public static class RoughSimilarity
{
    /// <summary>Shared genres, as a proportion of the seed's own.</summary>
    private const double GenreWeight = 70;

    /// <summary>Nearness in years, decaying over two decades.</summary>
    private const double YearWeight = 20;

    /// <summary>Nearness of the community rating, decaying over three points.</summary>
    private const double RatingWeight = 10;

    /// <summary>
    /// Ranks candidates against the seeds, best first, using the mean over the seeds of
    /// the same kind - the same rule Smart Similar's scored endpoint uses, so swapping
    /// engines changes the quality of the answer and not the shape of it.
    /// </summary>
    /// <param name="seeds">The chosen titles.</param>
    /// <param name="candidates">Everything that could be suggested; seeds are skipped.</param>
    /// <param name="minScore">Floor on the mean score.</param>
    /// <param name="limit">Maximum results, or 0 for all of them.</param>
    /// <returns>The ranking.</returns>
    public static IReadOnlyList<RoughMatch> Rank(
        IReadOnlyList<SimilarityInput> seeds,
        IEnumerable<SimilarityInput> candidates,
        double minScore,
        int limit)
    {
        var seedIds = seeds.Select(s => s.Id).ToHashSet();
        var ranked = new List<RoughMatch>();

        foreach (var candidate in candidates)
        {
            if (seedIds.Contains(candidate.Id))
            {
                continue;
            }

            double total = 0;
            int compared = 0;
            var sharedGenres = new List<string>();
            int? closestYear = null;

            foreach (var seed in seeds)
            {
                if (!string.Equals(seed.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                compared++;
                total += Score(seed, candidate, sharedGenres, ref closestYear);
            }

            if (compared == 0)
            {
                continue;
            }

            double mean = total / compared;
            if (mean < minScore)
            {
                continue;
            }

            ranked.Add(new RoughMatch(candidate.Id, candidate.Kind, Math.Round(mean, 2), sharedGenres, closestYear));
        }

        ranked.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Id.CompareTo(b.Id);
        });

        return limit > 0 && ranked.Count > limit ? ranked.GetRange(0, limit) : ranked;
    }

    private static double Score(
        SimilarityInput seed, SimilarityInput candidate, List<string> sharedGenres, ref int? closestYear)
    {
        double score = 0;

        if (seed.Genres.Count > 0)
        {
            int shared = 0;
            foreach (var genre in candidate.Genres)
            {
                if (seed.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    shared++;
                    if (!sharedGenres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    {
                        sharedGenres.Add(genre);
                    }
                }
            }

            score += GenreWeight * shared / seed.Genres.Count;
        }

        if (seed.Year.HasValue && candidate.Year.HasValue)
        {
            int gap = Math.Abs(seed.Year.Value - candidate.Year.Value);
            score += YearWeight * Math.Max(0, 1 - (gap / 20.0));

            if (!closestYear.HasValue || gap < closestYear.Value)
            {
                closestYear = gap;
            }
        }

        if (seed.CommunityRating.HasValue && candidate.CommunityRating.HasValue)
        {
            double gap = Math.Abs(seed.CommunityRating.Value - candidate.CommunityRating.Value);
            score += RatingWeight * Math.Max(0, 1 - (gap / 3.0));
        }

        return score;
    }
}

/// <summary>The little a rough score needs to know about an item.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Kind">"Movie" or "Series" - the two are never compared.</param>
/// <param name="Genres">Its genres.</param>
/// <param name="Year">Its production year, if known.</param>
/// <param name="CommunityRating">Its community rating, if known.</param>
public readonly record struct SimilarityInput(
    Guid Id, string Kind, IReadOnlyList<string> Genres, int? Year, float? CommunityRating);

/// <summary>One rough match.</summary>
/// <param name="Id">The candidate's item id.</param>
/// <param name="Kind">"Movie" or "Series".</param>
/// <param name="Score">The mean score over the comparable seeds, 0-100.</param>
/// <param name="SharedGenres">The genres it has in common with any seed.</param>
/// <param name="YearGap">The closest gap in years to any seed.</param>
public readonly record struct RoughMatch(
    Guid Id, string Kind, double Score, IReadOnlyList<string> SharedGenres, int? YearGap);
