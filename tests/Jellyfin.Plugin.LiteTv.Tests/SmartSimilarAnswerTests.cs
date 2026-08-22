using System.Text.Json;
using Jellyfin.Plugin.LiteTv.Integrations;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Reading Smart Similar's answer.
///
/// These exist because the answer was being thrown away in full over its very first field.
/// Jellyfin serialises ids as 32 hex characters with no dashes, System.Text.Json will only read
/// the dashed form back into a Guid, and the exception was caught and turned into "fall back to
/// the rough scorer" - so the suggestions worked, quietly, with the blunt engine, and nothing on
/// screen said otherwise.
/// </summary>
public class SmartSimilarAnswerTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new LooseGuidConverter() }
    };

    [Fact]
    public void ReadsTheDashlessIdsJellyfinActuallyWrites()
    {
        const string json = """
        {
          "Active": true,
          "Seeds": [ { "Id": "89e54cdd8e3068d2da6337694a4c99c5", "Name": "2 Fast 2 Furious", "Kind": "Movie", "Active": true, "Source": "Hybrid" } ],
          "Results": [ { "Id": "fb8ccf0e7a918b3c27e111ac81a0577e", "Kind": "Movie", "Score": 87.24 } ]
        }
        """;

        var answer = JsonSerializer.Deserialize<SmartSimilarScore>(json, Options);

        Assert.NotNull(answer);
        Assert.True(answer!.Active);
        Assert.Equal(new Guid("89e54cdd-8e30-68d2-da63-37694a4c99c5"), answer.Seeds[0].Id);
        Assert.Equal(new Guid("fb8ccf0e-7a91-8b3c-27e1-11ac81a0577e"), answer.Results[0].Id);
        Assert.Equal(87.24, answer.Results[0].Score);
    }

    [Fact]
    public void StillReadsTheDashedFormIfItEverArrives()
    {
        const string json = """
        { "Active": true, "Seeds": [], "Results": [ { "Id": "fb8ccf0e-7a91-8b3c-27e1-11ac81a0577e", "Kind": "Movie", "Score": 10 } ] }
        """;

        var answer = JsonSerializer.Deserialize<SmartSimilarScore>(json, Options);

        Assert.Equal(new Guid("fb8ccf0e-7a91-8b3c-27e1-11ac81a0577e"), answer!.Results[0].Id);
    }

    [Fact]
    public void AnUnreadableIdCostsThatOneFieldRatherThanTheWholeAnswer()
    {
        // The point of the converter: one bad id must not throw away a scored pool. It comes
        // back empty and the caller can skip it, which is what LiteTvController already does.
        const string json = """
        { "Active": true, "Seeds": [], "Results": [ { "Id": "not-an-id", "Kind": "Movie", "Score": 5 } ] }
        """;

        var answer = JsonSerializer.Deserialize<SmartSimilarScore>(json, Options);

        Assert.Equal(Guid.Empty, answer!.Results[0].Id);
        Assert.Equal(5, answer.Results[0].Score);
    }

    [Fact]
    public void ReadsTheSharedSignalsAScreenExplainsASuggestionWith()
    {
        const string json = """
        {
          "Active": true,
          "Seeds": [],
          "Results": [ {
            "Id": "fb8ccf0e7a918b3c27e111ac81a0577e",
            "Kind": "Movie",
            "Score": 87.24,
            "Shared": {
              "Genres": [ "Action", "Krimi" ],
              "People": [ "Paul Walker" ],
              "Studios": [ "Universal Pictures" ],
              "Tags": [ "car race" ],
              "YearGap": 2,
              "OfficialRating": true
            }
          } ]
        }
        """;

        var answer = JsonSerializer.Deserialize<SmartSimilarScore>(json, Options);
        var shared = answer!.Results[0].Shared;

        Assert.NotNull(shared);
        Assert.Equal(new[] { "Action", "Krimi" }, shared!.Genres);
        Assert.Equal(new[] { "Paul Walker" }, shared.People);
        Assert.Equal(2, shared.YearGap);
        Assert.True(shared.OfficialRating);
    }
}
