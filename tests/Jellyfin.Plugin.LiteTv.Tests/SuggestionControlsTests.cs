using Jellyfin.Plugin.LiteTv.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The controls the owner asked for after the suggestions produced a channel that mixed Marvel
/// series with <i>Balu und seine Crew</i> and expanded to 453 titles without saying so.
/// <para>
/// Both faults had the same shape: every signal the builder read said those titles belonged
/// together, and no signal said how big the result would be. So these cover the two signals that
/// were added - the age a title is made for, and the number of episodes it expands to - plus the
/// library filter, the family opt-in and the rotation that stops the same six ideas coming back.
/// </para>
/// </summary>
public class SuggestionControlsTests
{
    private static Series Show(string name, string rating, int episodes = 10, string[]? genres = null, string[]? studios = null) =>
        new()
        {
            Name = name,
            SortName = name,
            Id = Guid.NewGuid(),
            OfficialRating = rating,
            Genres = genres ?? ["Animation"],
            Studios = studios ?? []
        };

    private static Movie Film(string name, string rating, string[]? genres = null, string[]? studios = null) =>
        new()
        {
            Name = name,
            SortName = name,
            Id = Guid.NewGuid(),
            OfficialRating = rating,
            Genres = genres ?? ["Animation"],
            Studios = studios ?? [],
            RunTimeTicks = TimeSpan.FromMinutes(100).Ticks
        };

    /// <summary>Every series expands to this many episodes unless a test says otherwise.</summary>
    private static Func<Series, int> Episodes(int each) => _ => each;

    // ------------------------------------------------------------------ audience bands

    [Theory]
    [InlineData("FSK-0", AudienceBand.Child)]
    [InlineData("FSK 0", AudienceBand.Child)]
    [InlineData("G", AudienceBand.Child)]
    [InlineData("TV-Y", AudienceBand.Child)]
    [InlineData("FSK-6", AudienceBand.Family)]
    [InlineData("PG", AudienceBand.Family)]
    [InlineData("TV-Y7", AudienceBand.Family)]
    [InlineData("FSK-12", AudienceBand.Teen)]
    [InlineData("PG-13", AudienceBand.Teen)]
    [InlineData("de/16", AudienceBand.Teen)]
    [InlineData("FSK-18", AudienceBand.Adult)]
    [InlineData("R", AudienceBand.Adult)]
    [InlineData("TV-MA", AudienceBand.Adult)]
    [InlineData("", AudienceBand.Unknown)]
    [InlineData(null, AudienceBand.Unknown)]
    [InlineData("Not Rated", AudienceBand.Unknown)]
    internal void RatingsAreReadIntoBands(string? rating, AudienceBand expected)
    {
        Assert.Equal(expected, SuggestionAudience.Of(rating));
    }

    /// <summary>
    /// The named codes are read as codes, not as the number that happens to be in them.
    /// TV-Y7 is for seven-year-olds and NC-17 is for nobody under seventeen; read as ages they
    /// would swap places entirely.
    /// </summary>
    [Fact]
    public void ANumberInsideANamedCodeIsNotReadAsAnAge()
    {
        Assert.Equal(AudienceBand.Family, SuggestionAudience.Of("TV-Y7"));
        Assert.Equal(AudienceBand.Adult, SuggestionAudience.Of("NC-17"));
    }

    /// <summary>
    /// The complaint itself. A teen action channel must not pick up preschool programming just
    /// because both carry the same studio and the same genre word.
    /// </summary>
    [Fact]
    public void ATeenChannelDoesNotBorrowChildrensProgramming()
    {
        var series = new[]
        {
            Show("Marvel One", "FSK-12", studios: ["Marvel"]),
            Show("Marvel Two", "FSK-12", studios: ["Marvel"]),
            Show("Marvel Three", "FSK-16", studios: ["Marvel"]),
            Show("Balu und seine Crew", "FSK-0", studios: ["Disney"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Audience = AudienceBand.Teen },
            Episodes(5)).First();

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Balu und seine Crew");
    }

    /// <summary>
    /// And with no band asked for at all: a pool that is mostly about one audience is trimmed to
    /// it, so an unattended suggestion is coherent too. This is the case the owner actually hit -
    /// nobody had chosen anything.
    /// </summary>
    [Fact]
    public void AnUnaskedChannelStillSettlesOnOneAudience()
    {
        var series = new[]
        {
            Show("Teen One", "FSK-12", studios: ["Marvel"]),
            Show("Teen Two", "FSK-12", studios: ["Marvel"]),
            Show("Teen Three", "FSK-16", studios: ["Marvel"]),
            Show("Preschool", "FSK-0", studios: ["Marvel"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(series, [], [], null, Episodes(5))
            .Single(item => item.Name == "Marvel & Lucasfilm");

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Preschool");
        Assert.Equal("Jugendliche", suggestion.Reason.Audience);
    }

    /// <summary>
    /// A children's channel takes rated titles only. An unrated title is a title nobody checked,
    /// and the cost of being wrong is not symmetric.
    /// </summary>
    [Fact]
    public void AChildrensChannelLeavesUnratedTitlesOut()
    {
        var series = new[]
        {
            Show("Rated One", "FSK-0"),
            Show("Rated Two", "FSK-0"),
            Show("Rated Three", "FSK-0"),
            Show("Unrated", string.Empty)
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Audience = AudienceBand.Child },
            Episodes(5)).First();

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Unrated");
    }

    // ------------------------------------------------------------------ size

    /// <summary>
    /// The 453-title channel. The cap counts episodes rather than series, because a channel of
    /// four series is not small when they are four long-running ones.
    /// </summary>
    [Fact]
    public void AProposalIsHeldToTheSizeThatWasAllowed()
    {
        var series = Enumerable.Range(1, 8)
            .Select(index => Show("Show " + index, "FSK-12", genres: ["Action"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { MaxTitles = 40 },
            Episodes(12)).First();

        // Three of the eight fit; a fourth would be 48 titles against an allowance of 40.
        Assert.Equal(3, suggestion.Sources.Count);
        Assert.Equal(36, suggestion.Reason.EstimatedTitles);
        Assert.Equal(suggestion.Sources.Count, suggestion.Reason.SourceCount);
        Assert.Equal(40, suggestion.Reason.SizeLimit);
    }

    /// <summary>
    /// One enormous series must not shut out the ones that would have fitted behind it. It is
    /// skipped and the budget goes on being spent.
    /// </summary>
    [Fact]
    public void AnOversizedSourceIsSkippedRatherThanEndingTheChannel()
    {
        var series = new List<Series>
        {
            Show("Endless", "FSK-12", genres: ["Action"]),
            Show("Short one", "FSK-12", genres: ["Action"]),
            Show("Short two", "FSK-12", genres: ["Action"]),
            Show("Short three", "FSK-12", genres: ["Action"])
        };
        var lengths = new Dictionary<string, int>
        {
            ["Endless"] = 400,
            ["Short one"] = 6,
            ["Short two"] = 6,
            ["Short three"] = 6
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { MaxTitles = 30 },
            show => lengths[show.Name]).First();

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Endless");
        Assert.Equal(3, suggestion.Sources.Count);
        Assert.Equal(18, suggestion.Reason.EstimatedTitles);
    }

    /// <summary>
    /// A cap too small for any real channel offers nothing rather than a channel of two, which
    /// would loop the same title all evening.
    /// </summary>
    [Fact]
    public void AnImpossiblySmallCapOffersNothingRatherThanALoop()
    {
        var series = Enumerable.Range(1, 6)
            .Select(index => Show("Show " + index, "FSK-12", genres: ["Action"]))
            .ToList();

        var suggestions = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { MaxTitles = 5 }, Episodes(20));

        Assert.Empty(suggestions);
    }

    /// <summary>The film night has to fit inside the same allowance, not sit outside it.</summary>
    [Fact]
    public void TheFilmNightIsCountedAgainstTheSameAllowance()
    {
        var series = Enumerable.Range(1, 3)
            .Select(index => Show("Show " + index, "FSK-12", genres: ["Action"]))
            .ToList();
        var films = Enumerable.Range(1, 4)
            .Select(index => Film("Film " + index, "FSK-12", genres: ["Action"]))
            .ToList();

        var tight = ChannelSuggestionBuilder.Build(
            series, films, [], SuggestionOptions.Default with { MaxTitles = 12 }, Episodes(3)).First();
        var roomy = ChannelSuggestionBuilder.Build(
            series, films, [], SuggestionOptions.Default with { MaxTitles = 60 }, Episodes(3)).First();

        Assert.Null(tight.MovieNight);
        Assert.NotNull(roomy.MovieNight);
    }

    /// <summary>
    /// The floor and ceiling on sources were 3 and 12, which an owner reported as too little on
    /// any real library. They are 2 and 30 by default now, and the ceiling is a genuine ceiling
    /// rather than something every real pool hits.
    /// </summary>
    [Fact]
    public void TheDefaultSourceCeilingAllowsMoreThanTwelve()
    {
        var series = Enumerable.Range(1, 20)
            .Select(index => Show("Show " + index, "FSK-12", genres: ["Action"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { MaxTitles = 2000 }, Episodes(1)).First();

        Assert.True(suggestion.Sources.Count > 12, "expected more than the old ceiling of 12 sources");
    }

    /// <summary>Two sources are enough to be offered at all now, not just three.</summary>
    [Fact]
    public void TwoSourcesAreEnoughByDefault()
    {
        var series = new[]
        {
            Show("One", "FSK-12", genres: ["Action"]),
            Show("Two", "FSK-12", genres: ["Action"])
        };

        var suggestions = ChannelSuggestionBuilder.Build(series, [], [], SuggestionOptions.Default, Episodes(4));

        Assert.NotEmpty(suggestions);
    }

    // ------------------------------------------------------------------ families and rotation

    [Fact]
    public void OnlyTheFamiliesThatWereAskedForAreOffered()
    {
        var series = new[]
        {
            Show("Disney One", "FSK-6", studios: ["Walt Disney Pictures"]),
            Show("Disney Two", "FSK-6", studios: ["Pixar"]),
            Show("Disney Three", "FSK-0", studios: ["Disney"])
        };

        var studio = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            Episodes(4));
        var factualOnly = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Families = [SuggestionFamily.Factual] },
            Episodes(4));

        Assert.Contains(studio, item => item.Name == "Disney & Pixar");
        Assert.Empty(factualOnly);
    }

    /// <summary>
    /// A studio channel is offered only where the library's own metadata substantiates it. No
    /// borrowing an unrelated cartoon because it is also for children.
    /// </summary>
    [Fact]
    public void AStudioChannelIsOnlyBuiltFromThatStudio()
    {
        var series = new[]
        {
            Show("Dream One", "FSK-6", studios: ["DreamWorks Animation"]),
            Show("Dream Two", "FSK-6", studios: ["DreamWorks"]),
            Show("Dream Three", "FSK-6", studios: ["DreamWorks"]),
            Show("Somebody else", "FSK-6", studios: ["Studio Ghibli"])
        };

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Families = [SuggestionFamily.Studio] },
            Episodes(4)).Single(item => item.Name == "DreamWorks");

        Assert.DoesNotContain(suggestion.Sources, source => source.Name == "Somebody else");
        Assert.Equal(3, suggestion.Sources.Count);
    }

    /// <summary>
    /// Asking again offers different ideas rather than the same ones in a new order. The wheel
    /// turns; the ranking behind it does not change.
    /// </summary>
    [Fact]
    public void AskingAgainOffersDifferentIdeas()
    {
        var series = Enumerable.Range(1, 4).SelectMany(index => new[]
        {
            Show("Action " + index, "FSK-12", genres: ["Action"]),
            Show("Comedy " + index, "FSK-12", genres: ["Comedy"]),
            Show("Drama " + index, "FSK-12", genres: ["Drama"]),
            Show("Crime " + index, "FSK-12", genres: ["Crime"]),
            Show("Horror " + index, "FSK-18", genres: ["Horror"]),
            Show("Doku " + index, "FSK-12", genres: ["Documentary"])
        }).ToList();

        var first = ChannelSuggestionBuilder.Build(series, [], [], SuggestionOptions.Default, Episodes(4));
        var again = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Refresh = 1 }, Episodes(4));

        Assert.NotEqual(first.Select(item => item.Name), again.Select(item => item.Name));
    }

    [Fact]
    public void ADismissedIdeaIsNotOfferedAgain()
    {
        var series = Enumerable.Range(1, 5)
            .Select(index => Show("Action " + index, "FSK-12", genres: ["Action"]))
            .ToList();

        var suggestions = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default with { Dismissed = ["Action Arena"] }, Episodes(4));

        Assert.DoesNotContain(suggestions, item => item.Name == "Action Arena");
    }

    // ------------------------------------------------------------------ the stated reason

    [Fact]
    public void EveryProposalSaysWhereItCameFromAndHowBigItIs()
    {
        var series = Enumerable.Range(1, 4)
            .Select(index => Show("Doku " + index, "FSK-12", genres: ["Documentary"]))
            .ToList();

        var suggestion = ChannelSuggestionBuilder.Build(
            series, [], [], SuggestionOptions.Default, Episodes(7), ["Serien", "Filme"])
            .Single(item => item.Name == "Werkstatt & Wildnis");

        Assert.Equal(SuggestionFamily.Factual, suggestion.Reason.Family);
        Assert.Equal("Jugendliche", suggestion.Reason.Audience);
        Assert.Equal(["Serien", "Filme"], suggestion.Reason.Libraries);
        Assert.NotEmpty(suggestion.Reason.Because);
        Assert.Equal(4, suggestion.Reason.SourceCount);
        Assert.Equal(28, suggestion.Reason.EstimatedTitles);
    }

    /// <summary>
    /// The dominant band is decided by the titles that carry a rating. Letting unrated ones vote
    /// would hand the answer to the titles that know least, and a pool of mostly-unrated titles
    /// would come out as a children's channel by accident.
    /// </summary>
    [Fact]
    public void UnratedTitlesDoNotDecideTheBand()
    {
        var pool = new BaseItem[]
        {
            Show("Rated", "FSK-18"),
            Show("Also rated", "FSK-18"),
            Show("Unrated one", string.Empty),
            Show("Unrated two", string.Empty),
            Show("Unrated three", string.Empty)
        };

        Assert.Equal(AudienceBand.Adult, SuggestionAudience.Dominant(pool));
    }
}
