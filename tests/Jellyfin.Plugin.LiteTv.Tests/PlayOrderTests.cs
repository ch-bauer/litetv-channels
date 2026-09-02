using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class PlayOrderTests
{
    private static ScheduledEntry Entry(string name, string source) => new(
        Guid.NewGuid(), name, source, Guid.NewGuid(), TimeSpan.FromMinutes(20).Ticks)
    {
        SourceKey = source
    };

    [Fact]
    public void ShuffleBySourceShufflesEpisodesInsideEachSourceAndKeepsFixedSourceBlocks()
    {
        var entries = new[]
        {
            Entry("A1", "A"), Entry("B1", "B"), Entry("C1", "C"),
            Entry("A2", "A"), Entry("B2", "B"), Entry("C2", "C"),
            Entry("A3", "A"), Entry("B3", "B"), Entry("C3", "C"),
            Entry("A4", "A"), Entry("B4", "B"), Entry("C4", "C")
        };

        var result = ChannelPlaylistBuilder.Order(entries, PlayOrder.ShuffleBySource, Guid.Parse("11111111-1111-1111-1111-111111111111"), 0, 2, 20);

        Assert.Equal(12, result.Count);
        Assert.Equal(
            ["A", "A", "B", "B", "C", "C", "A", "A", "B", "B", "C", "C"],
            result.Select(entry => entry.SourceKey!).ToArray());
        foreach (var source in new[] { "A", "B", "C" })
        {
            Assert.Equal(
                [$"{source}1", $"{source}2", $"{source}3", $"{source}4"],
                result.Where(entry => entry.SourceKey == source).Select(entry => entry.Name).Order().ToArray());
        }
    }

    [Fact]
    public void WeightedShuffleAtOneHundredPercentStaysWithThePreviousSourceWhenPossible()
    {
        var entries = new[] { Entry("A1", "A"), Entry("A2", "A"), Entry("B1", "B"), Entry("B2", "B") };

        var result = ChannelPlaylistBuilder.Order(entries, PlayOrder.WeightedShuffle, Guid.Parse("22222222-2222-2222-2222-222222222222"), 0, 2, 100);

        Assert.Equal(entries.Length, result.Count);
        var switches = result.Zip(result.Skip(1), (first, second) => first.SourceKey != second.SourceKey).Count(changed => changed);
        Assert.Equal(1, switches);
    }
}
