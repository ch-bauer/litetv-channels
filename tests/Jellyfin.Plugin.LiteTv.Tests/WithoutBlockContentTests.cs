using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A block - a film night, a kids' hour - is a deliberate deviation from the channel's regular
/// rotation, not an extra helping of it. Before this, the same film chosen for both the
/// channel's own lineup and its Saturday film-night block aired twice: once in the loop, once
/// again in the block. <see cref="ChannelPlaylistBuilder.WithoutBlockContent"/> is the fix -
/// the same rule <see cref="Api.ChannelSuggestionBuilder"/> already applies when it composes a
/// suggestion, now applied to every channel regardless of how it was built or edited.
/// </summary>
public class WithoutBlockContentTests
{
    private static ChannelSource Source(Guid id, string name = "") =>
        new() { Type = ChannelSourceType.Movie, ItemId = id, Name = name };

    [Fact]
    public void AFilmInAMovieNightBlockIsNotAlsoInTheMainLineup()
    {
        var shared = Guid.NewGuid();
        var onlyInContent = Guid.NewGuid();

        var channel = new TvChannel
        {
            Sources = new List<ChannelSource> { Source(shared, "Shared"), Source(onlyInContent, "Only content") },
            Blocks = new List<ProgramBlock>
            {
                new()
                {
                    Enabled = true,
                    Sources = new List<ChannelSource> { Source(shared, "Shared") }
                }
            }
        };

        var result = ChannelPlaylistBuilder.WithoutBlockContent(channel);

        Assert.DoesNotContain(result, source => source.ItemId == shared);
        Assert.Contains(result, source => source.ItemId == onlyInContent);
    }

    /// <summary>A disabled block is not really playing anything, so it must not steal content from the loop.</summary>
    [Fact]
    public void ADisabledBlockDoesNotRemoveAnything()
    {
        var shared = Guid.NewGuid();
        var channel = new TvChannel
        {
            Sources = new List<ChannelSource> { Source(shared) },
            Blocks = new List<ProgramBlock>
            {
                new() { Enabled = false, Sources = new List<ChannelSource> { Source(shared) } }
            }
        };

        var result = ChannelPlaylistBuilder.WithoutBlockContent(channel);

        Assert.Contains(result, source => source.ItemId == shared);
    }

    /// <summary>No blocks at all is the ordinary channel, unchanged.</summary>
    [Fact]
    public void NoBlocksLeavesTheSourcesAsTheyWere()
    {
        var id = Guid.NewGuid();
        var channel = new TvChannel { Sources = new List<ChannelSource> { Source(id) } };

        var result = ChannelPlaylistBuilder.WithoutBlockContent(channel);

        Assert.Same(channel.Sources, result);
    }
}
