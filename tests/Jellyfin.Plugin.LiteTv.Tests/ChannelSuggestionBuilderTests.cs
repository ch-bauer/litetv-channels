using Jellyfin.Plugin.LiteTv.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class ChannelSuggestionBuilderTests
{
    [Fact]
    public void Build_ActionTemplateCarriesACompleteWeightedFilmNight()
    {
        var series = Enumerable.Range(1, 3).Select(index => new Series
        {
            Name = "Action series " + index,
            SortName = "Action series " + index,
            Id = Guid.NewGuid(),
            Genres = ["Action"]
        });
        var movies = Enumerable.Range(1, 3).Select(index => new Movie
        {
            Name = "Action film " + index,
            SortName = "Action film " + index,
            Id = Guid.NewGuid(),
            Genres = ["Action"],
            RunTimeTicks = TimeSpan.FromMinutes(100).Ticks
        });

        var suggestion = ChannelSuggestionBuilder.Build(series, movies, []).Single(item => item.Name == "Action Arena");

        Assert.Equal("WeightedShuffle", suggestion.Order);
        Assert.True(suggestion.RandomizeEpisodes);
        Assert.Equal(100, suggestion.Sources.Sum(source => source.Probability));
        Assert.NotNull(suggestion.MovieNight);
        Assert.Equal(100, suggestion.MovieNight!.Sources.Sum(source => source.Probability));
        Assert.True(suggestion.MovieNight.FitToContent);
        Assert.True(suggestion.MovieNight.ShiftToAvoidLeadingGap);
        Assert.True(suggestion.MovieNight.TrailerEnabled);
    }

    [Fact]
    public void Build_SkipsAChannelThatAlreadyExists()
    {
        var movies = Enumerable.Range(1, 4).Select(index => new Movie
        {
            Name = "Comedy film " + index,
            SortName = "Comedy film " + index,
            Id = Guid.NewGuid(),
            Genres = ["Comedy"],
            RunTimeTicks = TimeSpan.FromMinutes(100).Ticks
        });

        var suggestions = ChannelSuggestionBuilder.Build([], movies, ["Comedy & Chaos"]);

        Assert.DoesNotContain(suggestions, item => item.Name == "Comedy & Chaos");
    }
}
