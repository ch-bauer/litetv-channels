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
    public void ShuffleBySourceKeepsEachInterleaveGroupTogether()
    {
        var entries = new[] { Entry("A1", "A"), Entry("A2", "A"), Entry("B1", "B"), Entry("B2", "B") };

        var result = ChannelPlaylistBuilder.Order(entries, PlayOrder.ShuffleBySource, Guid.Parse("11111111-1111-1111-1111-111111111111"), 0, 2, 20);

        Assert.Equal(4, result.Count);
        var names = result.Select(entry => entry.Name).ToArray();
        Assert.True(names is ["A1", "A2", "B1", "B2"] or ["B1", "B2", "A1", "A2"]);
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
