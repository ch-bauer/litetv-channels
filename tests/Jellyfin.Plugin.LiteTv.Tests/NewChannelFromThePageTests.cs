using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiteTv.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The channel the configuration page makes, read the way the server reads it.
/// <para>
/// This file exists because of a fault that made the plugin unusable and that all 201 other
/// tests were blind to. The page's <c>addChannel</c> built a channel with
/// <c>Trailers: 'Between'</c> - a value <see cref="TrailerMode"/> has never had, invented when
/// the page became an app and typed only as <c>string</c>, so nothing on either side objected.
/// </para>
/// <para>
/// The consequence was out of all proportion to the typo. A plugin's configuration is saved as
/// <b>one document</b>: the page posts the whole thing, and one unparseable enum makes
/// System.Text.Json throw over the lot. So the server answered <c>500</c> and <b>nothing could
/// be saved at all</b> - not the new channel, and not the four that were already there and
/// perfectly valid. Creating a channel bricked the page.
/// </para>
/// <para>
/// The rule: <b>a value the page WRITES into configuration must be proven against the enum the
/// server reads it into.</b> The page's own type now says so too, but a union in TypeScript is
/// gone by the time the JSON lands, and this is the side that throws.
/// </para>
/// </summary>
public class NewChannelFromThePageTests
{
    /// <summary>Jellyfin writes and reads enums as names; so must this.</summary>
    private static readonly JsonSerializerOptions AsJellyfinReadsIt = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The literal from <c>web/src/lib/config.svelte.ts</c>, and it is read from the file rather
    /// than copied here. A copy would have gone on passing while the page shipped 'Between'.
    /// </summary>
    private static string PageSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.LiteTv.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var file = Path.Combine(dir!.FullName, "web", "src", "lib", "config.svelte.ts");
        Assert.True(File.Exists(file), file + " is where the page builds a new channel");
        return File.ReadAllText(file);
    }

    [Fact]
    public void EveryEnumTheNewChannelCarriesIsAMemberOfTheServersEnum()
    {
        var source = PageSource();

        foreach (var value in Literals(source, "Trailers"))
        {
            Assert.True(
                Enum.TryParse<TrailerMode>(value, out _),
                "The page writes Trailers: '" + value + "', which TrailerMode has no member for. "
                + "Saving ANY channel then fails, because the configuration is posted as one document.");
        }

        foreach (var value in Literals(source, "Order"))
        {
            Assert.True(
                Enum.TryParse<PlayOrder>(value, out _),
                "The page writes Order: '" + value + "', which PlayOrder has no member for.");
        }
    }

    /// <summary>
    /// The whole channel the page makes, parsed. Guards every other field at once - a number
    /// where a string belongs fails the same way, over the same whole document.
    /// </summary>
    [Fact]
    public void TheNewChannelParsesWhole()
    {
        var made = """
        {
            "Id": "b7f4b0e2-0000-4000-8000-000000000001",
            "Name": "New channel",
            "Enabled": true,
            "AnchorUtc": "2026-08-28T18:00:00.000Z",
            "Sources": [],
            "Adverts": [],
            "ScheduleEdits": [],
            "EpisodesPerBlock": 1,
            "Order": "Sequential",
            "SlotMinutes": 0,
            "TrailersInGaps": true,
            "Trailers": "Off",
            "TrailerEveryPrograms": 3,
            "TrailerLookahead": 3,
            "TrailerTitles": [],
            "Blocks": [],
            "TrailerSlots": [],
            "Artwork": {}
        }
        """;

        var channel = JsonSerializer.Deserialize<TvChannel>(made, AsJellyfinReadsIt);

        Assert.NotNull(channel);
        Assert.Equal("New channel", channel!.Name);
        Assert.Equal(TrailerMode.Off, channel.Trailers);
        Assert.Equal(PlayOrder.Sequential, channel.Order);
        Assert.Empty(channel.Sources);
    }

    private static System.Collections.Generic.IEnumerable<string> Literals(string source, string field)
    {
        foreach (Match match in Regex.Matches(source, field + @":\s*'([^']*)'"))
        {
            yield return match.Groups[1].Value;
        }
    }
}
