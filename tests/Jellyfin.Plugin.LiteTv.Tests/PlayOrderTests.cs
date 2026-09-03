using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class PlayOrderTests
{
    private static ScheduledEntry Entry(string name, string source, int probability = 100) => new(
        Guid.NewGuid(), name, source, Guid.NewGuid(), TimeSpan.FromMinutes(20).Ticks)
    {
        SourceKey = source,
        SourceProbability = probability
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

        var result = ChannelPlaylistBuilder.Order(entries, PlayOrder.ShuffleBySource, Guid.Parse("11111111-1111-1111-1111-111111111111"), 0, 2);

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
    public void WeightedShuffleUsesPerSourceWeightsAndBlockSize()
    {
        var entries = new[] { Entry("A1", "A", 100), Entry("A2", "A", 100), Entry("A3", "A", 100), Entry("B1", "B", 0), Entry("B2", "B", 0) };

        var result = ChannelPlaylistBuilder.Order(entries, PlayOrder.WeightedShuffle, Guid.Parse("22222222-2222-2222-2222-222222222222"), 0, 2);

        Assert.Equal(entries.Length, result.Count);
        Assert.Equal("A1", result[0].Name);
        Assert.Equal(["A", "A", "A", "B", "B"], result.Select(entry => entry.SourceKey!).ToArray());
    }

    [Fact]
    public void WeightedShuffleOnlyRepeatsASourceAfterEveryOtherPositiveSourceIsExhausted()
    {
        var entries = new[]
        {
            Entry("A1", "A", 50), Entry("A2", "A", 50), Entry("A3", "A", 50), Entry("A4", "A", 50),
            Entry("B1", "B", 10), Entry("B2", "B", 10), Entry("B3", "B", 10), Entry("B4", "B", 10),
            Entry("C1", "C", 20), Entry("C2", "C", 20), Entry("C3", "C", 20), Entry("C4", "C", 20)
        };

        var result = ChannelPlaylistBuilder.Order(
            entries,
            PlayOrder.WeightedShuffle,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            0,
            1);

        var remaining = entries
            .GroupBy(entry => entry.SourceKey!)
            .ToDictionary(group => group.Key, group => group.Count());
        string? previous = null;
        foreach (var entry in result)
        {
            var source = entry.SourceKey!;
            if (source == previous)
            {
                Assert.DoesNotContain(
                    remaining.Where(pair => pair.Key != source && pair.Value > 0),
                    pair => entries.First(candidate => candidate.SourceKey == pair.Key).SourceProbability > 0);
            }
            remaining[source]--;
            previous = source;
        }
    }

    [Fact]
    public void WeightedShuffleCanShuffleEpisodesInsideTheSourceBeforeTheLotteryDrawsIt()
    {
        var entries = new[]
        {
            Entry("A1", "A", 100), Entry("A2", "A", 100), Entry("A3", "A", 100), Entry("A4", "A", 100),
            Entry("B1", "B", 0), Entry("B2", "B", 0), Entry("B3", "B", 0), Entry("B4", "B", 0)
        };

        var result = ChannelPlaylistBuilder.Order(
            entries,
            PlayOrder.WeightedShuffle,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            0,
            2,
            randomizeEpisodes: true);

        Assert.Equal(entries.Length, result.Count);
        Assert.Equal(entries.Select(entry => entry.Name).Order(), result.Select(entry => entry.Name).Order());
        Assert.NotEqual(
            ["A1", "A2", "A3", "A4"],
            result.Where(entry => entry.SourceKey == "A").Select(entry => entry.Name).ToArray());
    }
}
