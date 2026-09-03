using System.Globalization;
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

    private static readonly ChannelSource SecondPlaylist = new()
    {
        Type = ChannelSourceType.YouTube,
        Name = "Playlist B",
        Url = "https://www.youtube.com/playlist?list=PLbbbbbbbbbbbbbbbbbbbbbb",
        Probability = 30
    };

    private static readonly Guid AChannel = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>Videos named after their playlist and position, so an order can be read.</summary>
    private static List<ScheduledEntry> Videos(ChannelSource source, string label, int count) =>
        ChannelPlaylistBuilder.YouTubeEntries(
            source,
            Enumerable.Range(1, count)
                .Select(index => new YouTubePlaylist.Item(
                    label.ToLowerInvariant() + "vid" + index.ToString("D8", CultureInfo.InvariantCulture),
                    label + index.ToString(CultureInfo.InvariantCulture),
                    60))
                .ToList()).ToList();

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

    /// <summary>
    /// Sequential is the mode with no shuffle in it at all, and a playlist's own order is the
    /// order YouTube returned. Worth pinning through <c>Order</c> rather than only through
    /// <c>YouTubeEntries</c>: the modes are told apart inside <c>Order</c>, and a playlist
    /// reaching a shuffle branch by accident would still look plausible in a guide.
    /// </summary>
    [Fact]
    public void SequentialLeavesBothPlaylistsInTheirConfiguredOrder()
    {
        var entries = Videos(Playlist, "A", 3).Concat(Videos(SecondPlaylist, "B", 3)).ToList();

        var ordered = ChannelPlaylistBuilder.Order(entries, PlayOrder.Sequential, AChannel, 0, 2);

        Assert.Equal(["A1", "A2", "A3", "B1", "B2", "B3"], ordered.Select(entry => entry.Name));
    }

    /// <summary>
    /// Shuffle each source: the videos inside a playlist move, the playlists themselves do not.
    /// The blocks must arrive A, B, A, B in the configured order - a mode that also chose which
    /// source came next would be weighted random wearing this mode's name.
    /// </summary>
    [Fact]
    public void ShuffleBySourceMovesVideosInsideAPlaylistButNotThePlaylistOrder()
    {
        var entries = Videos(Playlist, "A", 4).Concat(Videos(SecondPlaylist, "B", 4)).ToList();

        var ordered = ChannelPlaylistBuilder.Order(entries, PlayOrder.ShuffleBySource, AChannel, 0, 2);

        Assert.Equal(
            [Playlist.Url, Playlist.Url, SecondPlaylist.Url, SecondPlaylist.Url,
             Playlist.Url, Playlist.Url, SecondPlaylist.Url, SecondPlaylist.Url],
            ordered.Select(entry => entry.SourceKey));

        // Every video still airs exactly once, whatever order it landed in.
        Assert.Equal(
            ["A1", "A2", "A3", "A4", "B1", "B2", "B3", "B4"],
            ordered.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));

        // And it is a shuffle, not a copy of the input: at least one source moved.
        var untouched = ordered.Select(entry => entry.Name).SequenceEqual(
            ["A1", "A2", "B1", "B2", "A3", "A4", "B3", "B4"]);
        Assert.False(untouched);
    }

    /// <summary>
    /// The same channel and the same videos must produce the same schedule every time. A guide
    /// that promises one video and a player that airs another is the failure this prevents, and
    /// it only appears across processes - so the seed may never come from the clock or from
    /// <see cref="Random"/>'s default.
    /// </summary>
    [Theory]
    [InlineData(PlayOrder.ShuffleBySource)]
    [InlineData(PlayOrder.WeightedShuffle)]
    public void TheSameChannelDrawsTheSameOrderEveryTime(PlayOrder order)
    {
        var first = ChannelPlaylistBuilder.Order(
            Videos(Playlist, "A", 4).Concat(Videos(SecondPlaylist, "B", 4)).ToList(),
            order, AChannel, 0, 2);
        var second = ChannelPlaylistBuilder.Order(
            Videos(Playlist, "A", 4).Concat(Videos(SecondPlaylist, "B", 4)).ToList(),
            order, AChannel, 0, 2);

        Assert.Equal(first.Select(entry => entry.Name), second.Select(entry => entry.Name));
    }

    /// <summary>
    /// A different channel must not inherit the first one's order, or every channel built from
    /// the same playlists would air the same evening.
    /// </summary>
    [Fact]
    public void AnotherChannelDrawsItsOwnOrder()
    {
        var here = ChannelPlaylistBuilder.Order(
            Videos(Playlist, "A", 6).Concat(Videos(SecondPlaylist, "B", 6)).ToList(),
            PlayOrder.ShuffleBySource, AChannel, 0, 2);
        var elsewhere = ChannelPlaylistBuilder.Order(
            Videos(Playlist, "A", 6).Concat(Videos(SecondPlaylist, "B", 6)).ToList(),
            PlayOrder.ShuffleBySource,
            Guid.Parse("55555555-5555-5555-5555-555555555555"), 0, 2);

        Assert.NotEqual(here.Select(entry => entry.Name), elsewhere.Select(entry => entry.Name));
    }

    /// <summary>
    /// A playlist normally carries a few videos that cannot be timed - members-only, removed,
    /// still premiering. They are dropped on the way in, and the blocks that follow must be
    /// counted from what is left rather than from what YouTube listed: counting the dropped
    /// ones would hand a source a short block and shift every block after it.
    /// </summary>
    [Fact]
    public void UnplayableVideosLeaveTheBlocksFullRatherThanShort()
    {
        var withGaps = ChannelPlaylistBuilder.YouTubeEntries(
            Playlist,
            [
                new YouTubePlaylist.Item("aaavid00000001", "A1", 60),
                new YouTubePlaylist.Item("removed0000001", "Removed", 0),
                new YouTubePlaylist.Item("aaavid00000002", "A2", 60),
                new YouTubePlaylist.Item("removed0000002", "Also removed", 0),
                new YouTubePlaylist.Item("aaavid00000003", "A3", 60),
                new YouTubePlaylist.Item("aaavid00000004", "A4", 60)
            ]).ToList();

        var ordered = ChannelPlaylistBuilder.Order(
            withGaps.Concat(Videos(SecondPlaylist, "B", 4)).ToList(),
            PlayOrder.ShuffleBySource, AChannel, 0, 2);

        Assert.Equal(
            [Playlist.Url, Playlist.Url, SecondPlaylist.Url, SecondPlaylist.Url,
             Playlist.Url, Playlist.Url, SecondPlaylist.Url, SecondPlaylist.Url],
            ordered.Select(entry => entry.SourceKey));
    }
}
