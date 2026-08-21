using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A hand-made edit bends the generated schedule around it. Everything here is arithmetic on
/// the clock, which is exactly where an off-by-one survives for weeks: the guide still looks
/// plausible, and the channel plays the wrong minute of the right programme.
/// </summary>
public class ScheduleEditingTests
{
    private static readonly DateTime Noon = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FilmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static Airing Program(string name, DateTime start, int minutes) =>
        new(
            AiringKind.Program,
            new ScheduledEntry(Guid.NewGuid(), name, null, null, TimeSpan.FromMinutes(minutes).Ticks),
            start,
            start.AddMinutes(minutes),
            0,
            null,
            null);

    private static IReadOnlyList<Airing> Apply(IEnumerable<Airing> airings, params ScheduleEdit[] edits) =>
        ScheduleEditing.Apply(
            airings,
            edits,
            Noon.AddHours(-1),
            Noon.AddHours(6),
            _ => TimeSpan.FromMinutes(90).Ticks,
            _ => "The Film",
            "Test channel").ToList();

    [Fact]
    public void NoEdits_ChangesNothing()
    {
        var generated = new[] { Program("A", Noon, 30), Program("B", Noon.AddMinutes(30), 30) };

        Assert.Equal(generated, Apply(generated));
    }

    /// <summary>An edit that covers a programme entirely takes its place.</summary>
    [Fact]
    public void AnEditReplacesWhatItCovers()
    {
        var result = Apply(
            new[] { Program("A", Noon, 30), Program("B", Noon.AddMinutes(30), 30) },
            new ScheduleEdit
            {
                StartUtc = Noon.AddMinutes(30),
                Kind = ScheduleEditKind.Air,
                Url = "https://example.invalid/advert",
                DurationSeconds = 1800
            });

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Entry!.Name);
        Assert.Equal(AiringKind.Interstitial, result[1].Kind);
        Assert.Equal("https://example.invalid/advert", result[1].TrailerUrl);
    }

    /// <summary>
    /// An edit in the middle leaves the two halves either side of it, and the second half has
    /// to start where the first left off - otherwise the channel replays what it just skipped.
    /// </summary>
    [Fact]
    public void AnEditInTheMiddleKeepsBothHalvesAndTheOffset()
    {
        var result = Apply(
            new[] { Program("Long film", Noon, 120) },
            new ScheduleEdit
            {
                StartUtc = Noon.AddMinutes(40),
                Kind = ScheduleEditKind.Air,
                Url = "https://example.invalid/advert",
                DurationSeconds = 600
            });

        Assert.Equal(3, result.Count);

        Assert.Equal(Noon, result[0].StartUtc);
        Assert.Equal(Noon.AddMinutes(40), result[0].EndUtc);
        Assert.Equal(0, result[0].OffsetTicks);

        Assert.Equal(AiringKind.Interstitial, result[1].Kind);

        Assert.Equal(Noon.AddMinutes(50), result[2].StartUtc);
        Assert.Equal(Noon.AddMinutes(120), result[2].EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(50).Ticks, result[2].OffsetTicks);
    }

    /// <summary>A library item brings its own runtime, and that is what it displaces.</summary>
    [Fact]
    public void ALibraryItemTakesItsOwnRuntime()
    {
        var result = Apply(
            new[] { Program("A", Noon, 30), Program("B", Noon.AddMinutes(30), 30), Program("C", Noon.AddHours(1), 60) },
            new ScheduleEdit { StartUtc = Noon, Kind = ScheduleEditKind.Air, ItemId = FilmId });

        Assert.Equal("The Film", result[0].Entry!.Name);
        Assert.Equal(Noon.AddMinutes(90), result[0].EndUtc);

        // The 90 minutes swallowed A and B whole and took half of C.
        Assert.Equal(2, result.Count);
        Assert.Equal("C", result[1].Entry!.Name);
        Assert.Equal(Noon.AddMinutes(90), result[1].StartUtc);
    }

    /// <summary>Removing leaves a hole rather than pulling the schedule forward.</summary>
    [Fact]
    public void RemovingLeavesTheChannelDark()
    {
        var result = Apply(
            new[] { Program("A", Noon, 30), Program("B", Noon.AddMinutes(30), 30) },
            new ScheduleEdit
            {
                StartUtc = Noon,
                Kind = ScheduleEditKind.Remove,
                DurationSeconds = 1800
            });

        Assert.Single(result);
        Assert.Equal("B", result[0].Entry!.Name);
    }

    /// <summary>A sliver left by an edit is not worth airing.</summary>
    [Fact]
    public void ASliverIsDroppedRatherThanAired()
    {
        var result = Apply(
            new[] { Program("A", Noon, 30) },
            new ScheduleEdit
            {
                // Leaves one minute of A at the front, which is less than the minimum.
                StartUtc = Noon.AddMinutes(1),
                Kind = ScheduleEditKind.Air,
                Url = "https://example.invalid/advert",
                DurationSeconds = 1740
            });

        Assert.Single(result);
        Assert.Equal(AiringKind.Interstitial, result[0].Kind);
    }

    [Fact]
    public void ADisabledEditDoesNothing()
    {
        var generated = new[] { Program("A", Noon, 30) };
        var result = Apply(
            generated,
            new ScheduleEdit
            {
                Enabled = false,
                StartUtc = Noon,
                Kind = ScheduleEditKind.Remove,
                DurationSeconds = 1800
            });

        Assert.Equal(generated, result);
    }

    /// <summary>Edits outside the window asked for are not the window's business.</summary>
    [Fact]
    public void AnEditOutsideTheWindowIsIgnored()
    {
        var generated = new[] { Program("A", Noon, 30) };
        var result = Apply(
            generated,
            new ScheduleEdit
            {
                StartUtc = Noon.AddDays(3),
                Kind = ScheduleEditKind.Remove,
                DurationSeconds = 1800
            });

        Assert.Equal(generated, result);
    }

    /// <summary>Two edits in a row, which is what a break full of adverts looks like.</summary>
    [Fact]
    public void EditsBackToBackBothLand()
    {
        var result = Apply(
            new[] { Program("Long film", Noon, 120) },
            new ScheduleEdit
            {
                StartUtc = Noon.AddMinutes(30),
                Kind = ScheduleEditKind.Air,
                Url = "https://example.invalid/one",
                DurationSeconds = 300
            },
            new ScheduleEdit
            {
                StartUtc = Noon.AddMinutes(35),
                Kind = ScheduleEditKind.Air,
                Url = "https://example.invalid/two",
                DurationSeconds = 300
            });

        Assert.Equal(4, result.Count);
        Assert.Equal("https://example.invalid/one", result[1].TrailerUrl);
        Assert.Equal("https://example.invalid/two", result[2].TrailerUrl);
        Assert.Equal(Noon.AddMinutes(40), result[3].StartUtc);
    }
}
