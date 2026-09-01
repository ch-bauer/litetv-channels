using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class ChannelScheduleTests
{
    /// <summary>A Monday, so the block windows below line up with the week the timeline counts in.</summary>
    private static readonly DateTime Anchor = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ScheduledEntry Entry(string name, int minutes, bool trailer = false)
        => new(Guid.NewGuid(), name, null, null, TimeSpan.FromMinutes(minutes).Ticks)
        {
            IsTrailer = trailer
        };

    private static Lineup Queue(int slotMinutes, params (string Name, int Minutes)[] entries)
        => new(entries.Select(e => Entry(e.Name, e.Minutes)).ToList(), TimeSpan.FromMinutes(slotMinutes).Ticks);

    private static ChannelSchedule Schedule(
        IReadOnlyList<BlockWindow> blocks,
        params (int Owner, Lineup Lineup)[] lineups)
    {
        return new ChannelSchedule(
            WeekTimeline.Build(blocks),
            lineups.ToDictionary(l => l.Owner, l => l.Lineup),
            blocks.ToDictionary(b => b.Owner, b => "Block " + b.Owner),
            Anchor,
            TimeZoneInfo.Utc);
    }

    private static ChannelSchedule Simple(Lineup lineup)
        => Schedule(Array.Empty<BlockWindow>(), (WeekTimeline.BaseLineup, lineup));

    private static BlockWindow Block(int owner, int startHour, int hours, params DayOfWeek[] days)
        => new(owner, startHour * 60, hours * 60, days);

    // ------------------------------------------------------------------ the plain loop

    [Fact]
    public void At_Anchor_StartsTheQueue()
    {
        var airing = Simple(Queue(0, ("A", 30), ("B", 60))).At(Anchor);

        Assert.NotNull(airing);
        Assert.Equal(AiringKind.Program, airing!.Kind);
        Assert.Equal("A", airing.Entry!.Name);
        Assert.Equal(0, airing.OffsetTicks);
        Assert.Equal(Anchor, airing.StartUtc);
    }

    [Fact]
    public void ScheduledTrailer_IsReportedAsTrailer()
    {
        var schedule = Simple(new Lineup(
            new[] { Entry("Film", 30), Entry("Trailer", 2, trailer: true) },
            0));

        var airing = schedule.At(Anchor.AddMinutes(30));

        Assert.NotNull(airing);
        Assert.Equal(AiringKind.Trailer, airing!.Kind);
        Assert.Equal("Trailer", airing.Entry!.Name);
    }

    [Fact]
    public void At_MidQueue_IsInsideTheSecondProgram()
    {
        var airing = Simple(Queue(0, ("A", 30), ("B", 60))).At(Anchor.AddMinutes(45));

        Assert.Equal("B", airing!.Entry!.Name);
        Assert.Equal(Anchor.AddMinutes(30), airing.StartUtc);
        Assert.Equal(Anchor.AddMinutes(90), airing.EndUtc);

        // The airing began at its own start; a viewer joining now comes in 15 minutes late.
        Assert.Equal(0, airing.OffsetTicks);
        Assert.Equal(TimeSpan.FromMinutes(15).Ticks, airing.OffsetAt(Anchor.AddMinutes(45)));
    }

    [Fact]
    public void At_PastTheEnd_LoopsRoundToTheStart()
    {
        var now = Anchor.AddMinutes(90 + 10);
        var airing = Simple(Queue(0, ("A", 30), ("B", 60))).At(now);

        Assert.Equal("A", airing!.Entry!.Name);
        Assert.Equal(Anchor.AddMinutes(90), airing.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, airing.OffsetAt(now));
    }

    // ------------------------------------------------------------------ fixed slots

    [Fact]
    public void At_WithSlots_LeavesTheRestOfTheSlotAsInterstitial()
    {
        var schedule = Simple(Queue(30, ("A", 20), ("B", 20)));

        var program = schedule.At(Anchor.AddMinutes(5));
        Assert.Equal(AiringKind.Program, program!.Kind);
        Assert.Equal(Anchor.AddMinutes(20), program.EndUtc);

        var gap = schedule.At(Anchor.AddMinutes(25));
        Assert.Equal(AiringKind.Interstitial, gap!.Kind);
        Assert.Equal(Anchor.AddMinutes(20), gap.StartUtc);
        Assert.Equal(Anchor.AddMinutes(30), gap.EndUtc);
        Assert.Equal("B", gap.NextProgram!.Name);
    }

    [Fact]
    public void At_WithSlots_StartsEveryProgramOnTheGrid()
    {
        var schedule = Simple(Queue(30, ("A", 20), ("B", 20)));

        Assert.Equal("B", schedule.At(Anchor.AddMinutes(30))!.Entry!.Name);
        Assert.Equal("A", schedule.At(Anchor.AddMinutes(60))!.Entry!.Name);
    }

    [Fact]
    public void At_ProgramLongerThanASlot_TakesWholeSlots()
    {
        // 50 minutes on a 30-minute grid occupies two slots, so the next one starts at 60.
        var schedule = Simple(Queue(30, ("Long", 50), ("B", 20)));

        Assert.Equal("Long", schedule.At(Anchor.AddMinutes(45))!.Entry!.Name);
        Assert.Equal(AiringKind.Interstitial, schedule.At(Anchor.AddMinutes(55))!.Kind);
        Assert.Equal("B", schedule.At(Anchor.AddMinutes(60))!.Entry!.Name);
    }

    /// <summary>
    /// The point of the whole slot feature: a channel can promise the film at 20:15.
    /// </summary>
    [Fact]
    public void At_BlockStartingAtQuarterPastEight_AirsTheFilmAtQuarterPastEight()
    {
        var schedule = Schedule(
            new[] { new BlockWindow(0, (20 * 60) + 15, 180, Array.Empty<DayOfWeek>()) },
            (WeekTimeline.BaseLineup, Queue(0, ("Filler", 30))),
            (0, Queue(0, ("Film", 100))));

        var airing = schedule.At(Anchor.AddHours(20).AddMinutes(15));

        Assert.Equal("Film", airing!.Entry!.Name);
        Assert.Equal(0, airing.OffsetTicks);
        Assert.Equal(Anchor.AddHours(20).AddMinutes(15), airing.StartUtc);
    }

    // ------------------------------------------------------------------ program blocks

    [Fact]
    public void At_InsideABlock_AirsTheBlocksOwnLineup()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("Kids", 60))));

        Assert.Equal("Kids", schedule.At(Anchor.AddHours(7))!.Entry!.Name);
        Assert.Equal("Base", schedule.At(Anchor.AddHours(13))!.Entry!.Name);
        Assert.Equal("Base", schedule.At(Anchor.AddHours(3))!.Entry!.Name);
    }

    [Fact]
    public void At_ABlockEnding_CutsTheProgramAndHandsOver()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("Kids", 60))));

        // The block runs to 12:00, so the airing at 11:30 cannot run past it.
        var airing = schedule.At(Anchor.AddHours(11).AddMinutes(30));

        Assert.Equal("Kids", airing!.Entry!.Name);
        Assert.Equal(Anchor.AddHours(12), airing.EndUtc);
    }

    [Fact]
    public void At_ABlockComingRoundAgain_ResumesWhereItWasCutOff()
    {
        // One eight-hour program in a six-hour block: it cannot finish in one sitting.
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("Epic", 480))));

        var nextDay = schedule.At(Anchor.AddDays(1).AddHours(6).AddMinutes(30));

        Assert.Equal("Epic", nextDay!.Entry!.Name);
        // Six hours aired yesterday, half an hour today.
        Assert.Equal(TimeSpan.FromMinutes(390).Ticks, nextDay.OffsetTicks + TimeSpan.FromMinutes(30).Ticks);
        Assert.Equal(TimeSpan.FromMinutes(360).Ticks, nextDay.OffsetTicks);
    }

    [Fact]
    public void At_BlockOnOneWeekdayOnly_LeavesTheOtherDaysToTheBase()
    {
        var schedule = Schedule(
            new[] { Block(0, 20, 3, DayOfWeek.Saturday) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("FilmNight", 120))));

        // The anchor is a Monday; Saturday is five days on.
        Assert.Equal("FilmNight", schedule.At(Anchor.AddDays(5).AddHours(21))!.Entry!.Name);
        Assert.Equal("Base", schedule.At(Anchor.AddDays(4).AddHours(21))!.Entry!.Name);
    }

    [Fact]
    public void At_BlockRunningPastMidnight_CarriesIntoTheNextDay()
    {
        var schedule = Schedule(
            new[] { Block(0, 22, 4, DayOfWeek.Saturday) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("LateNight", 60))));

        Assert.Equal("LateNight", schedule.At(Anchor.AddDays(5).AddHours(23))!.Entry!.Name);
        Assert.Equal("LateNight", schedule.At(Anchor.AddDays(6).AddHours(1))!.Entry!.Name);
        Assert.Equal("Base", schedule.At(Anchor.AddDays(6).AddHours(3))!.Entry!.Name);
    }

    [Fact]
    public void At_OverlappingBlocks_TheFirstOneConfiguredWins()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6), Block(1, 10, 6) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("First", 60))),
            (1, Queue(0, ("Second", 60))));

        Assert.Equal("First", schedule.At(Anchor.AddHours(11))!.Entry!.Name);
        Assert.Equal("Second", schedule.At(Anchor.AddHours(13))!.Entry!.Name);
    }

    [Fact]
    public void At_NoLineupForThePartOfTheWeek_IsOffAir()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (0, Queue(0, ("Kids", 60))));

        var airing = schedule.At(Anchor.AddHours(13));

        Assert.Equal(AiringKind.OffAir, airing!.Kind);
        Assert.Null(airing.Entry);
        // Off air until the block comes round again at 06:00.
        Assert.Equal(Anchor.AddDays(1).AddHours(6), airing.EndUtc);
    }

    // ------------------------------------------------------------------ walking a window

    [Fact]
    public void Enumerate_CoversTheWindowWithoutGapsOrOverlaps()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (WeekTimeline.BaseLineup, Queue(30, ("Base", 20))),
            (0, Queue(0, ("Kids", 45))));

        var airings = schedule.Enumerate(Anchor, Anchor.AddHours(24)).ToList();

        Assert.NotEmpty(airings);
        for (var i = 1; i < airings.Count; i++)
        {
            Assert.Equal(airings[i - 1].EndUtc, airings[i].StartUtc);
            Assert.True(airings[i].EndUtc > airings[i].StartUtc, "every airing takes time");
        }

        Assert.True(airings[0].StartUtc <= Anchor);
        Assert.True(airings[^1].EndUtc >= Anchor.AddHours(24));
    }

    [Fact]
    public void Enumerate_HandsOverExactlyAtTheBlockBoundary()
    {
        var schedule = Schedule(
            new[] { Block(0, 6, 6) },
            (WeekTimeline.BaseLineup, Queue(0, ("Base", 60))),
            (0, Queue(0, ("Kids", 45))));

        var airings = schedule.Enumerate(Anchor.AddHours(5), Anchor.AddHours(7)).ToList();
        var handover = airings.First(a => a.Entry?.Name == "Kids");

        Assert.Equal(Anchor.AddHours(6), handover.StartUtc);
        Assert.Equal("Base", airings.Last(a => a.StartUtc < Anchor.AddHours(6)).Entry!.Name);
    }
}
