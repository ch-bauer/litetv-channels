using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LiteTv.Api;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The edit run as it arrives from the page.
/// <para>
/// This file exists because of a fault that shipped in 1.0.77.0 and that every other test in
/// this project was blind to. <c>WeekEditsDto.Edits</c> was get-only, like every other
/// collection on these DTOs - which is right for the ones that are only ever serialised out, and
/// fatal for the one that is deserialised in: <b>System.Text.Json cannot fill a collection it
/// has no way to assign</b>, so it skipped the property and handed the action an empty run.
/// </para>
/// <para>
/// Nothing failed. No exception, no 400, no line in the log. The endpoint answered 200 with the
/// week exactly as stored, for every run anybody sent - so on screen an edit appeared, the
/// rehearsal came back without it, and the edit undid itself. Save wrote nothing. The fold
/// underneath it was correct all along and eight tests said so, because they all built their
/// runs in C# and never once crossed the wire.
/// </para>
/// <para>
/// The rule: <b>a DTO the server READS needs a test that parses JSON.</b> Testing the method it
/// feeds proves the method, not the binding.
/// </para>
/// </summary>
public class WeekEditsOverTheWireTests
{
    /// <summary>How ASP.NET reads a body: camelCase out, case-insensitive in.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WeekEditsDto Parse(string json)
        => JsonSerializer.Deserialize<WeekEditsDto>(json, Web) ?? new WeekEditsDto();

    [Fact]
    public void ARunOfEditsSurvivesBeingParsed()
    {
        // The exact body the page sends.
        var parsed = Parse("""
            {"Edits":[
              {"Kind":"Remove","AiringId":"11111111-1111-4111-8111-111111111111"},
              {"Kind":"Place","Airing":{"Id":null,"StartSecond":72000,"DurationSeconds":2700,"Kind":"Programme","Name":"A"}}
            ]}
            """);

        Assert.Equal(2, parsed.Edits.Count);
        Assert.Equal("Remove", parsed.Edits[0].Kind);
        Assert.NotNull(parsed.Edits[0].AiringId);
        Assert.Equal("Place", parsed.Edits[1].Kind);
        Assert.Equal(72000, parsed.Edits[1].Airing!.StartSecond);
        Assert.Equal("A", parsed.Edits[1].Airing!.Name);
    }

    [Fact]
    public void TheLengthEditCarriesItsNumber()
    {
        // The one that was noticed first on a real server: a fortnight rehearsed as a week.
        var parsed = Parse("""{"Edits":[{"Kind":"Length","Weeks":2}]}""");

        Assert.Single(parsed.Edits);
        Assert.Equal(2, parsed.Edits[0].Weeks);
    }

    [Fact]
    public void CamelCaseIsReadToo()
    {
        // ASP.NET matches case-insensitively, so a page that ever sends camelCase still works.
        var parsed = Parse("""{"edits":[{"kind":"Clear"}]}""");

        Assert.Single(parsed.Edits);
        Assert.Equal("Clear", parsed.Edits[0].Kind);
    }

    [Fact]
    public void AnEmptyRunParsesAsEmpty_NotAsMissing()
    {
        // The page sends this when the last pending edit is undone, and it must mean "the week
        // as stored" - which is what the whole run being silently dropped ALSO looked like.
        Assert.Empty(Parse("""{"Edits":[]}""").Edits);
    }

    [Fact]
    public void AParsedRunActuallyReachesTheFold()
    {
        // End to end over the shape that failed: parse a body, run it, see the effect. A test
        // that builds the run in C# cannot tell the difference between a working endpoint and
        // one that drops every edit on the floor.
        var parsed = Parse("""{"Edits":[{"Kind":"Length","Weeks":3}]}""");

        var after = LiteTvController.RunEdits(
            new Core.StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 },
            Guid.NewGuid(),
            parsed.Edits,
            weeks => new Core.StoredWeek { Weeks = weeks },
            _ => 1800);

        Assert.Equal(3, after!.Weeks);
    }
}
