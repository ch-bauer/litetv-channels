using System.Text.Json;
using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class YouTubeTrailerSearchTests
{
    [Fact]
    public void Harvest_TakesTrailerVideosAndSkipsNonTrailerVideos()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "contents": [
                { "videoRenderer": { "videoId": "not-a-trailer", "title": { "simpleText": "A behind the scenes clip" } } },
                { "videoRenderer": { "videoId": "german-trailer", "title": { "runs": [ { "text": "Dune: Part Two – Offizieller Trailer Deutsch" } ] } } }
              ]
            }
            """);

        var results = YouTubeTrailerSearch.Harvest(document.RootElement);

        var result = Assert.Single(results);
        Assert.Equal("Dune: Part Two – Offizieller Trailer Deutsch", result.Name);
        Assert.Equal("https://www.youtube.com/watch?v=german-trailer", result.Url);
    }
}
