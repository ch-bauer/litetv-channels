using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class CollectionOrderTests
{
    [Fact]
    public void CollectionItemsRemainInTheOrderReturnedByTheCollection()
    {
        var items = new[] { "Fast & Furious", "2 Fast 2 Furious", "Tokyo Drift" };

        Assert.Equal(items, ChannelPlaylistBuilder.CollectionChildrenInStoredOrder(items));
    }
}
