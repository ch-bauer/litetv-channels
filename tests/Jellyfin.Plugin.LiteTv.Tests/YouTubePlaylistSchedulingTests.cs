using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Covers the boundary between a real YouTube playlist response and source-aware scheduling.
/// Playlist videos must carry the one configured source identity into every order mode; otherwise
/// a playlist looks like unrelated one-item sources after it reaches the schedule builder.
/// </summary>
public class YouTubePlaylistSchedulingTests
{
    private static readonly ChannelSource Playlist = new()
    {
        Type = ChannelSourceType.YouTube,
        Name = "Playlist A",
        Url = "https://www.youtube.com/playlist?list=PLaaaaaaaaaaaaaaaaaaaaaa",
        Probability = 70
    };

    [Fact]
    public void PlaylistEntriesKeepPlaylistOrderAndOneStableSourceIdentity()
    {
        var entries = ChannelPlaylistBuilder.YouTubeEntries(
            Playlist,
            [
                new YouTubePlaylist.Item("firstvideo01", "First", 120),
                new YouTubePlaylist.Item("missingtime", "Unavailable", 0),
                new YouTubePlaylist.Item("thirdvideo03", "Third", 180)
            ]);

        Assert.Equal(["First", "Third"], entries.Select(entry => entry.Name));
        Assert.All(entries, entry =>
        {
            Assert.Equal(Playlist.Url, entry.SourceKey);
            Assert.Equal(70, entry.SourceProbability);
            Assert.True(entry.IsAddress);
        });
    }

    [Fact]
    public void PlaylistEntriesParticipateInFixedSourceBlocks()
    {
        var playlist = ChannelPlaylistBuilder.YouTubeEntries(
            Playlist,
            [
                new YouTubePlaylist.Item("one00000001", "P1", 60),
                new YouTubePlaylist.Item("two00000002", "P2", 60),
                new YouTubePlaylist.Item("three000003", "P3", 60),
                new YouTubePlaylist.Item("four0000004", "P4", 60)
            ]);
        var library = Enumerable.Range(1, 4).Select(index => new ScheduledEntry(
            Guid.NewGuid(),
            "L" + index,
            "Library",
            Guid.NewGuid(),
            TimeSpan.FromMinutes(20).Ticks)
        {
            SourceKey = "library",
            SourceProbability = 30
        });

        var ordered = ChannelPlaylistBuilder.Order(
            playlist.Concat(library).ToList(),
            PlayOrder.ShuffleBySource,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            0,
            2);

        Assert.Equal(
            [Playlist.Url, Playlist.Url, "library", "library", Playlist.Url, Playlist.Url, "library", "library"],
            ordered.Select(entry => entry.SourceKey));
    }
}
