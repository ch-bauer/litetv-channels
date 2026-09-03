using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Integrations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Sharing a studio is not being the same kind of thing.
/// <para>
/// The report: a DreamWorks channel that held <i>Catch Me If You Can</i> beside
/// <i>Kung Fu Panda</i>. Both really are DreamWorks - and on the first the studio is not even the
/// one anybody thinks of. Studio metadata alone was the whole test, so a caper and a cartoon came
/// out as one channel.
/// </para>
/// <para>
/// The pool is trimmed a second time now, to the titles that resemble the one it is mostly about.
/// The real answer comes from the Smart Similar plugin over HTTP; these tests supply the same
/// shape of answer with <see cref="RoughSimilarity"/>, which is also what the server falls back to
/// when that plugin is absent.
/// </para>
/// </summary>
public class SuggestionCohesionTests
{
    private static Movie Film(string name, int year, string[] genres, string[]? studios = null) =>
        new()
        {
            Name = name,
            SortName = name,
            Id = Guid.NewGuid(),
            OfficialRating = "FSK-6",
            ProductionYear = year,
            Genres = genres,
            Studios = studios ?? ["DreamWorks Animation"],
            RunTimeTicks = TimeSpan.FromMinutes(95).Ticks
        };

    private static Series Show(string name, int year, string[] genres, string[]? studios = null) =>
        new()
        {
            Name = name,
            SortName = name,
            Id = Guid.NewGuid(),
            OfficialRating = "FSK-6",
            ProductionYear = year,
            Genres = genres,
            Studios = studios ?? ["DreamWorks Animation"]
        };

    /// <summary>
    /// The cohesion the server uses when Smart Similar is not installed, wired the same way: the
    /// pool is ranked against its anchor and everything below the floor is dropped.
    /// </summary>
    private static Func<IReadOnlyList<BaseItem>, BaseItem, IReadOnlyList<BaseItem>> Roughly(int floor) =>
        (pool, anchor) =>
        {
            var seeds = new[] { Input(anchor) };
            var keep = RoughSimilarity.Rank(seeds, pool.Select(Input), floor, 0)
                .Select(match => match.Id)
                .ToHashSet();
            keep.Add(anchor.Id);
            return pool.Where(item => keep.Contains(item.Id)).ToList();
        };

    private static SimilarityInput Input(BaseItem item) => new(
        item.Id,
        item is Series ? "Series" : "Movie",
        (item.Genres ?? Array.Empty<string>()).ToList(),
        item.ProductionYear,
        item.CommunityRating);

    /// <summary>The complaint itself, as a fixture.</summary>
    [Fact]
    public void AStudioChannelKeepsOneKindOfThing()
    {
        var pool = new List<Movie>
        {
            Film("Kung Fu Panda", 2008, ["Animation", "Family", "Comedy"]),
            Film("Madagascar", 2005, ["Animation", "Family", "Comedy"]),
            Film("Shrek", 2001, ["Animation", "Family", "Comedy"]),
            Film("Die Croods", 2013, ["Animation", "Family", "Comedy"]),
            Film("Catch Me If You Can", 2002, ["Drama", "Crime"], ["DreamWorks Pictures"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            [], pool, [],
            SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            _ => 1,
            null,
            Roughly(40)).Single(item => item.Name == "DreamWorks");

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Catch Me If You Can");
        Assert.Equal(4, suggestion.Sources.Count);
    }

    /// <summary>
    /// And the other way round: a pool that is mostly the caper keeps the caper and drops the
    /// cartoon. The rule is "resembles what this pool is about", not "is animation".
    /// </summary>
    [Fact]
    public void TheMajorityOfThePoolDecidesWhatItIsAbout()
    {
        var pool = new List<Movie>
        {
            Film("Catch Me If You Can", 2002, ["Drama", "Crime"], ["DreamWorks Pictures"]),
            Film("Road to Perdition", 2002, ["Drama", "Crime"], ["DreamWorks Pictures"]),
            Film("The Terminal", 2004, ["Drama", "Comedy"], ["DreamWorks Pictures"]),
            Film("Gladiator", 2000, ["Drama", "Action"], ["DreamWorks Pictures"]),
            Film("Kung Fu Panda", 2008, ["Animation", "Family"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            [], pool, [],
            SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            _ => 1,
            null,
            Roughly(40)).Single(item => item.Name == "DreamWorks");

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Kung Fu Panda");
        Assert.Contains(suggestion.Sources, source => source.Name == "Catch Me If You Can");
    }

    /// <summary>
    /// Strictness is the owner's dial. Loose enough and the pool stays broad; tight enough and
    /// only the closest relatives survive. It has to be the same pool that changes, or the
    /// setting is decoration.
    /// </summary>
    [Fact]
    public void StrictnessDecidesHowBroadTheChannelIs()
    {
        var pool = new List<Movie>
        {
            Film("Animation one", 2008, ["Animation", "Family", "Comedy"]),
            Film("Animation two", 2009, ["Animation", "Family", "Comedy"]),
            Film("Animation three", 2010, ["Animation", "Family", "Comedy"]),
            Film("Family comedy", 2009, ["Family", "Comedy"]),
            Film("Straight comedy", 2011, ["Comedy"])
        };

        var loose = ChannelSuggestionBuilder.Build(
            [], pool, [], SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            _ => 1, null, Roughly(20)).Single(item => item.Name == "DreamWorks");
        var tight = ChannelSuggestionBuilder.Build(
            [], pool, [], SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            _ => 1, null, Roughly(75)).Single(item => item.Name == "DreamWorks");

        Assert.True(
            tight.Sources.Count < loose.Sources.Count,
            "tight " + tight.Sources.Count + " vs loose " + loose.Sources.Count);
    }

    /// <summary>
    /// Cohesion may not shrink a channel out of existence. Below the floor the pool is kept as it
    /// was: an idea that is a bit broad beats no idea at all, and the size cap is the setting
    /// meant for cutting things down.
    /// </summary>
    [Fact]
    public void CohesionNeverTakesAChannelBelowItsMinimum()
    {
        var pool = new List<Movie>
        {
            Film("One", 2001, ["Animation"]),
            Film("Two", 2002, ["Drama"]),
            Film("Three", 2003, ["Horror"]),
            Film("Four", 2004, ["Documentary"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            [], pool, [], SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            _ => 1, null, Roughly(99)).SingleOrDefault(item => item.Name == "DreamWorks");

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.Sources.Count >= 3);
    }

    // ------------------------------------------------------------------ the film night

    /// <summary>
    /// A film channel is films. A block of films inside it is the same evening twice, so it never
    /// gets one however the setting is left.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("on")]
    public void AFilmChannelNeverGetsAFilmNight(string filmNight)
    {
        var films = Enumerable.Range(1, 8)
            .Select(index => Film("Film " + index, 2000 + index, ["Action"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            [], films, [],
            SuggestionOptions.Default with { Families = [SuggestionFamily.Film], FilmNight = filmNight },
            _ => 1).Single(item => item.Name == "Filmkanal");

        Assert.Null(suggestion.MovieNight);
    }

    /// <summary>
    /// Everywhere else the films are <b>reserved</b> for the block rather than also being the
    /// channel's content. The same title on twice was the other half of the report.
    /// </summary>
    [Fact]
    public void TheFilmNightsFilmsAreNotAlsoTheChannelsContent()
    {
        var series = Enumerable.Range(1, 4)
            .Select(index => Show("Doku " + index, 2010 + index, ["Documentary"]))
            .ToList();
        var films = Enumerable.Range(1, 4)
            .Select(index => Film("Naturfilm " + index, 2015 + index, ["Documentary"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, films, [], SuggestionOptions.Default, _ => 4)
            .Single(item => item.Name == "Werkstatt & Wildnis");

        Assert.NotNull(suggestion.MovieNight);

        var content = suggestion.Sources.Select(source => source.ItemId).ToHashSet();
        var night = suggestion.MovieNight!.Sources.Select(source => source.ItemId).ToHashSet();
        Assert.Empty(content.Intersect(night));
        Assert.All(suggestion.Sources, source => Assert.Equal("Series", source.Type));
    }

    [Fact]
    public void TheFilmNightCanBeTurnedOff()
    {
        var series = Enumerable.Range(1, 4)
            .Select(index => Show("Doku " + index, 2010 + index, ["Documentary"]))
            .ToList();
        var films = Enumerable.Range(1, 4)
            .Select(index => Film("Naturfilm " + index, 2015 + index, ["Documentary"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, films, [], SuggestionOptions.Default with { FilmNight = "off" }, _ => 4)
            .Single(item => item.Name == "Werkstatt & Wildnis");

        Assert.Null(suggestion.MovieNight);

        // And with no block to reserve them for, the films are content again rather than lost.
        Assert.Contains(suggestion.Sources, source => source.Type == "Movie");
    }

    // ------------------------------------------------------------------ the other settings

    [Fact]
    public void TrailersAndEpisodeShufflingCanBeTurnedOff()
    {
        var series = Enumerable.Range(1, 5)
            .Select(index => Show("Show " + index, 2010 + index, ["Action"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [],
            SuggestionOptions.Default with { Trailers = false, RandomizeEpisodes = false },
            _ => 4).First();

        Assert.Equal("Off", suggestion.Trailers);
        Assert.False(suggestion.RandomizeEpisodes);
        Assert.DoesNotContain("Trailer-Vorschau", suggestion.Features);
    }

    [Fact]
    public void TheSourceCountStaysInsideTheChosenBounds()
    {
        var series = Enumerable.Range(1, 20)
            .Select(index => Show("Show " + index, 2010, ["Action"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [],
            SuggestionOptions.Default with { MinSources = 4, MaxSources = 5, MaxTitles = 500 },
            _ => 4).First();

        Assert.InRange(suggestion.Sources.Count, 4, 5);
    }

    /// <summary>
    /// The preview shown before a channel is added is built from what is already on the wire, so
    /// every source has to carry enough to name itself.
    /// </summary>
    [Fact]
    public void EverySourceCarriesWhatThePreviewNeeds()
    {
        var series = Enumerable.Range(1, 4)
            .Select(index => Show("Show " + index, 2012, ["Action", "Adventure"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default, _ => 7).First();

        Assert.All(suggestion.Sources, source =>
        {
            Assert.NotEmpty(source.Name);
            Assert.Equal(2012, source.Year);
            Assert.NotEmpty(source.Genres);
            Assert.Equal(7, source.Titles);
        });
    }
}
