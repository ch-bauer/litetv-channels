using Jellyfin.Plugin.LiteTv.Integrations;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The fallback used when the Smart Similar plugin is not installed. It is meant to be
/// dumb, but it must be dumb in the same <em>shape</em> as the real one - the mean over
/// comparable seeds, kinds never mixed - or a suggestions screen would behave differently
/// depending on which plugins happen to be present.
/// </summary>
public class RoughSimilarityTests
{
    [Fact]
    public void Rank_SharedGenresCarryTheAnswer()
    {
        var seed = Movie("Heat", new[] { "Crime", "Thriller" }, 1995);
        var close = Movie("Collateral", new[] { "Crime", "Thriller" }, 2004);
        var partial = Movie("Drive", new[] { "Thriller" }, 2011);
        var unrelated = Movie("Cars", new[] { "Animation" }, 2006);

        var ranked = RoughSimilarity.Rank(new[] { seed }, new[] { seed, close, partial, unrelated }, 0, 0);

        Assert.Equal(close.Id, ranked[0].Id);
        Assert.Equal(partial.Id, ranked[1].Id);
        Assert.Equal(unrelated.Id, ranked[2].Id);
        Assert.DoesNotContain(ranked, m => m.Id == seed.Id);
    }

    [Fact]
    public void Rank_MeansOverTheSeeds_SoMatchingAllOfThemWins()
    {
        var seedA = Movie("A", new[] { "Crime" }, 2000);
        var seedB = Movie("B", new[] { "Comedy" }, 2000);
        var bothGenres = Movie("Both", new[] { "Crime", "Comedy" }, 2000);
        var oneGenre = Movie("One", new[] { "Crime" }, 2000);

        var ranked = RoughSimilarity.Rank(
            new[] { seedA, seedB }, new[] { bothGenres, oneGenre }, 0, 0);

        Assert.Equal(bothGenres.Id, ranked[0].Id);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    [Fact]
    public void Rank_NeverComparesASeriesWithAFilm()
    {
        var film = Movie("Film", new[] { "Crime" }, 2000);
        var series = new SimilarityInput(Guid.NewGuid(), "Series", new[] { "Crime" }, 2000, null);

        var ranked = RoughSimilarity.Rank(new[] { film }, new[] { series }, 0, 0);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_FloorAndLimitApplyToTheMean()
    {
        var seed = Movie("Seed", new[] { "Crime" }, 2000);
        var strong = Movie("Strong", new[] { "Crime" }, 2000);
        var weak = Movie("Weak", new[] { "Comedy" }, 1960);

        Assert.Equal(2, RoughSimilarity.Rank(new[] { seed }, new[] { strong, weak }, 0, 0).Count);
        Assert.Single(RoughSimilarity.Rank(new[] { seed }, new[] { strong, weak }, 50, 0));
        Assert.Single(RoughSimilarity.Rank(new[] { seed }, new[] { strong, weak }, 0, 1));
    }

    [Fact]
    public void Rank_ReportsWhatItMatchedOn()
    {
        var seed = Movie("Seed", new[] { "Crime", "Thriller" }, 1995);
        var other = Movie("Other", new[] { "Thriller", "Drama" }, 2001);

        var match = RoughSimilarity.Rank(new[] { seed }, new[] { other }, 0, 0).Single();

        Assert.Equal(new[] { "Thriller" }, match.SharedGenres);
        Assert.Equal(6, match.YearGap);
    }

    [Fact]
    public void Rank_AnUnratedUndatedItem_IsStillRankedOnItsGenres()
    {
        // Sparse metadata is the normal case for the titles nobody has scraped;
        // they must not fall out of the answer entirely.
        var seed = Movie("Seed", new[] { "Horror" }, 1980, 7.0f);
        var sparse = new SimilarityInput(Guid.NewGuid(), "Movie", new[] { "Horror" }, null, null);

        var match = RoughSimilarity.Rank(new[] { seed }, new[] { sparse }, 0, 0).Single();

        Assert.Equal(70, match.Score);
        Assert.Null(match.YearGap);
    }

    private static SimilarityInput Movie(string name, string[] genres, int year, float? rating = null)
    {
        _ = name;
        return new SimilarityInput(Guid.NewGuid(), "Movie", genres, year, rating);
    }
}
