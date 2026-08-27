using System.Text.Json;
using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Reading a YouTube playlist, which is what lets a playlist be a channel's content rather than
/// only something inside a break.
/// <para>
/// The network half cannot be tested here, so what is tested is the two halves that go wrong
/// silently: finding the playlist id in whatever was pasted, and pulling the videos out of a
/// response whose shape YouTube reshuffles regularly.
/// </para>
/// </summary>
public class YouTubePlaylistTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9", "PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9", "PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9")]
    [InlineData("https://m.youtube.com/playlist?list=UUrAXtmRdnEQy6nuLMHjMZOz5", "UUrAXtmRdnEQy6nuLMHjMZOz5")]
    [InlineData("PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9", "PLrAXtmRdnEQy6nuLMHjMZOz59Oq8B9Bj9")]
    public void APlaylistIdIsFoundWhereverItIs(string url, string expected)
    {
        Assert.Equal(expected, YouTubePlaylist.PlaylistId(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("not a url at all")]
    public void SomethingThatIsNotAPlaylistIsNotOne(string? url)
    {
        Assert.Null(YouTubePlaylist.PlaylistId(url));
    }

    [Fact]
    public void AVideoAddressIsNotMistakenForAPlaylist()
    {
        // A watch link with no list= must not come back with the video id, which would send the
        // playlist reader off to browse something that does not exist.
        Assert.Null(YouTubePlaylist.PlaylistId("https://youtu.be/dQw4w9WgXcQ"));
    }

    /// <summary>
    /// The renderer, wrapped in enough scaffolding to stand in for the real response - which
    /// nests it differently from one month to the next. Harvesting walks for the renderer
    /// rather than addressing it by path, and this is the test of that.
    /// </summary>
    private static JsonElement Response(string body)
        => JsonDocument.Parse(body).RootElement;

    [Fact]
    public void VideosAreFoundHoweverDeeplyTheyAreBuried()
    {
        var json = Response("""
        {
          "contents": { "twoColumnBrowseResultsRenderer": { "tabs": [ { "tabRenderer": {
            "content": { "sectionListRenderer": { "contents": [ { "itemSectionRenderer": {
              "contents": [ { "playlistVideoListRenderer": { "contents": [
                { "playlistVideoRenderer": {
                    "videoId": "aaaaaaaaaaa",
                    "title": { "runs": [ { "text": "First " }, { "text": "video" } ] },
                    "lengthSeconds": "212"
                } },
                { "playlistVideoRenderer": {
                    "videoId": "bbbbbbbbbbb",
                    "title": { "simpleText": "Second video" },
                    "lengthSeconds": "95"
                } }
              ] } } ] } } ] } } } } ] } }
        }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(2, items.Count);

        // Runs are joined, not just the first taken.
        Assert.Equal("First video", items[0].Title);
        Assert.Equal(212, items[0].Seconds);
        Assert.Equal("https://www.youtube.com/watch?v=aaaaaaaaaaa", items[0].Url);

        Assert.Equal("Second video", items[1].Title);
        Assert.Equal(95, items[1].Seconds);
    }

    /// <summary>
    /// The shape YouTube actually sends, taken from a real playlist on 27 Aug 2026.
    /// <para>
    /// There is not one <c>playlistVideoRenderer</c> in that response any more - every entry is
    /// a <c>lockupViewModel</c>, with the id in <c>contentId</c>, the title two levels down in
    /// <c>lockupMetadataViewModel</c>, and the length only as the badge drawn on the thumbnail.
    /// Reading zero videos from a real playlist is what this looked like from outside, and it
    /// was silent: no exception, no warning, just an empty playlist.
    /// </para>
    /// </summary>
    [Fact]
    public void TodaysShape_IsRead()
    {
        var json = Response("""
        { "contents": { "x": [ { "lockupViewModel": {
            "contentId": "fNk_zzaMoSs",
            "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage": { "thumbnailViewModel": { "overlays": [ { "thumbnailBottomOverlayViewModel": {
              "badges": [ { "thumbnailBadgeViewModel": { "text": "9:52" } } ] } } ] } },
            "metadata": { "lockupMetadataViewModel": {
              "title": { "content": "Vectors | Chapter 1" } } }
        } } ] } }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        var only = Assert.Single(items);
        Assert.Equal("fNk_zzaMoSs", only.VideoId);
        Assert.Equal("Vectors | Chapter 1", only.Title);
        Assert.Equal(9 * 60 + 52, only.Seconds);
        Assert.Equal("https://www.youtube.com/watch?v=fNk_zzaMoSs", only.Url);
    }

    [Fact]
    public void ALockupThatIsNotAVideo_IsNotScheduled()
    {
        // A playlist page also lockups channels and other playlists. Airing one as a programme
        // is worse than skipping it.
        var json = Response("""
        { "x": [
          { "lockupViewModel": {
              "contentId": "UCsomethingchannel",
              "contentType": "LOCKUP_CONTENT_TYPE_CHANNEL",
              "metadata": { "lockupMetadataViewModel": { "title": { "content": "Some channel" } } } } },
          { "lockupViewModel": {
              "contentId": "ccccccccccc",
              "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
              "metadata": { "lockupMetadataViewModel": { "title": { "content": "A video" } } } } }
        ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        var only = Assert.Single(items);
        Assert.Equal("ccccccccccc", only.VideoId);
        // No badge at all - a live entry looks like this, and it is kept, saying zero.
        Assert.Equal(0, only.Seconds);
    }

    [Fact]
    public void ABadgeThatIsNotADuration_IsNotReadAsOne()
    {
        var json = Response("""
        { "x": [ { "lockupViewModel": {
            "contentId": "ddddddddddd",
            "contentType": "LOCKUP_CONTENT_TYPE_VIDEO",
            "contentImage": { "thumbnailViewModel": { "overlays": [ { "thumbnailBottomOverlayViewModel": {
              "badges": [
                { "thumbnailBadgeViewModel": { "text": "4K" } },
                { "thumbnailBadgeViewModel": { "text": "1:04:11" } } ] } } ] } },
            "metadata": { "lockupMetadataViewModel": { "title": { "content": "A long one" } } }
        } } ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(3851, Assert.Single(items).Seconds);
    }

    [Fact]
    public void TheVisibleDurationIsUsedWhenLengthSecondsIsAbsent()
    {
        var json = Response("""
        { "x": [ { "playlistVideoRenderer": {
            "videoId": "ccccccccccc",
            "title": { "simpleText": "No lengthSeconds" },
            "lengthText": { "simpleText": "1:02:03" }
        } } ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Single(items);
        Assert.Equal((1 * 3600) + (2 * 60) + 3, items[0].Seconds);
    }

    [Fact]
    public void AVideoWithNoLengthAtAllIsKeptButSaysSoWithZero()
    {
        // Kept here and dropped by the builder, which is where the reasoning about schedules
        // belongs: reading a playlist should report what is in it, not make scheduling decisions.
        var json = Response("""
        { "x": [ { "playlistVideoRenderer": {
            "videoId": "ddddddddddd",
            "title": { "simpleText": "Upcoming premiere" }
        } } ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Single(items);
        Assert.Equal(0, items[0].Seconds);
    }

    [Fact]
    public void TheSameVideoTwiceIsTakenOnce()
    {
        // A continuation can repeat what the first page already gave, and a playlist may
        // genuinely list the same video twice. Either way the queue should not stutter.
        var json = Response("""
        { "x": [
            { "playlistVideoRenderer": { "videoId": "eeeeeeeeeee", "title": { "simpleText": "One" }, "lengthSeconds": "60" } },
            { "playlistVideoRenderer": { "videoId": "eeeeeeeeeee", "title": { "simpleText": "One again" }, "lengthSeconds": "60" } }
        ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Single(items);
    }

    [Fact]
    public void TheContinuationTokenIsPickedUp()
    {
        var json = Response("""
        {
          "x": [ { "playlistVideoRenderer": { "videoId": "fffffffffff", "title": { "simpleText": "One" }, "lengthSeconds": "60" } } ],
          "continuations": { "reloadContinuationData": {
            "continuationEndpoint": { "continuationCommand": { "token": "NEXT-PAGE-TOKEN" } }
          } }
        }
        """);

        var items = new List<YouTubePlaylist.Item>();
        var token = YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal("NEXT-PAGE-TOKEN", token);
    }

    [Fact]
    public void NoContinuationMeansNoMorePages()
    {
        var json = Response("""
        { "x": [ { "playlistVideoRenderer": { "videoId": "ggggggggggg", "title": { "simpleText": "Only" }, "lengthSeconds": "60" } } ] }
        """);

        var items = new List<YouTubePlaylist.Item>();
        var token = YouTubePlaylist.Harvest(json, items, new HashSet<string>(StringComparer.Ordinal));

        Assert.Null(token);
    }
}
