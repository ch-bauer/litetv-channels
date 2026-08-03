using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class EpisodeOrderTests
{
    private static Episode Ep(int? season, int? number, string path = "")
    {
        return new Episode { ParentIndexNumber = season, IndexNumber = number, Path = path };
    }

    private static string Describe(Episode episode)
        => "S" + (episode.ParentIndexNumber?.ToString() ?? "-") + "E" + (episode.IndexNumber?.ToString() ?? "-");

    [Fact]
    public void InAiredOrder_SortsBySeasonThenEpisode()
    {
        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(2, 1), Ep(1, 2), Ep(1, 1) })
            .Select(Describe)
            .ToList();

        Assert.Equal(new[] { "S1E1", "S1E2", "S2E1" }, ordered);
    }

    [Fact]
    public void InAiredOrder_PutsSpecialsAfterNumberedSeasons()
    {
        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(0, 3), Ep(2, 1), Ep(1, 1) })
            .Select(Describe)
            .ToList();

        Assert.Equal(new[] { "S1E1", "S2E1", "S0E3" }, ordered);
    }

    /// <summary>
    /// The case that took a channel off the air: a series whose episodes all sit in season
    /// 0 - which is where the server files everything it cannot place - must still air.
    /// </summary>
    [Fact]
    public void InAiredOrder_KeepsSeriesThatIsAllSpecials()
    {
        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(0, 4), Ep(0, 1), Ep(0, 22) })
            .Select(Describe)
            .ToList();

        Assert.Equal(new[] { "S0E1", "S0E4", "S0E22" }, ordered);
    }

    [Fact]
    public void InAiredOrder_PlacesSpecialBeforeTheEpisodeItAirsBefore()
    {
        var prologue = Ep(0, 7);
        prologue.AirsBeforeSeasonNumber = 2;
        prologue.AirsBeforeEpisodeNumber = 1;

        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(2, 1), Ep(1, 1), prologue })
            .Select(Describe)
            .ToList();

        Assert.Equal(new[] { "S1E1", "S0E7", "S2E1" }, ordered);
    }

    [Fact]
    public void InAiredOrder_PlacesSpecialAfterTheSeasonItAirsAfter()
    {
        var recap = Ep(0, 9);
        recap.AirsAfterSeasonNumber = 1;

        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(2, 1), recap, Ep(1, 2), Ep(1, 1) })
            .Select(Describe)
            .ToList();

        Assert.Equal(new[] { "S1E1", "S1E2", "S0E9", "S2E1" }, ordered);
    }

    [Fact]
    public void InAiredOrder_BreaksTiesByPathSoRebuildsAgree()
    {
        var ordered = ChannelPlaylistBuilder.InAiredOrder(new[] { Ep(0, 1, "/b/S00E01.mkv"), Ep(0, 1, "/a/S00E01.mkv") })
            .Select(e => e.Path)
            .ToList();

        Assert.Equal(new[] { "/a/S00E01.mkv", "/b/S00E01.mkv" }, ordered);
    }
}
