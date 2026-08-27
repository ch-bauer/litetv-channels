using System;
using System.Linq;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A schedule that runs for more than one week before it repeats.
/// <para>
/// The owner asked for it in as many words: a channel whose whole schedule is two weeks or
/// more, because a fortnightly film cannot be said in seven days however the seven days are
/// arranged. Everything here used to be arithmetic on a circle exactly one week round, and the
/// week was a constant in eight places - which is why this file exists: the interesting failures
/// are all off-by-a-week, and a schedule that is wrong by a week looks completely plausible for
/// seven days.
/// </para>
/// </summary>
public class LongerThanAWeekTests
{
    private const int Hour = 3600;
    private const int Day = 24 * Hour;
    private const int Week = 7 * Day;

    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

    private static StoredAiring Row(int startSecond, int seconds, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            StartSecond = startSecond,
            DurationSeconds = seconds,
            Kind = StoredAiringKind.Programme,
            ItemId = Guid.NewGuid(),
            Name = name
        };

    /// <summary>A fortnight with a film on the second Monday evening and nothing on the first.</summary>
    private static StoredWeek Fortnight()
    {
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 2 };
        week.Airings.Add(Row(20 * Hour, 2 * Hour, "first Monday"));
        week.Airings.Add(Row(Week + (20 * Hour), 2 * Hour, "second Monday"));
        return week;
    }

    private static DateTime Utc(StoredWeek week, int secondOfCycle, DateTime around)
    {
        var start = WeekReader.CycleStart(
            DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(around, Berlin), DateTimeKind.Unspecified),
            week.Weeks);
        return TimeZoneInfo.ConvertTimeToUtc(start.AddSeconds(secondOfCycle), Berlin);
    }

    [Fact]
    public void TheTwoWeeksAirDifferentThings()
    {
        // The whole point. Week one at nine is one film and week two at nine is the other, and
        // a cycle that was still seven days round would air the same thing on both.
        var week = Fortnight();
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        var first = WeekReader.At(week, Utc(week, (20 * Hour) + 600, now), Berlin);
        var second = WeekReader.At(week, Utc(week, Week + (20 * Hour) + 600, now), Berlin);

        Assert.Equal("first Monday", first?.Entry?.Name);
        Assert.Equal("second Monday", second?.Entry?.Name);
    }

    [Fact]
    public void TheCycleRepeatsAfterTwoWeeks_NotOne()
    {
        var week = Fortnight();
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var at = Utc(week, (20 * Hour) + 600, now);

        Assert.Equal("first Monday", WeekReader.At(week, at, Berlin)?.Entry?.Name);

        // A week later is the OTHER week of the cycle...
        Assert.Equal("second Monday", WeekReader.At(week, at.AddDays(7), Berlin)?.Entry?.Name);

        // ...and a fortnight later is where it started.
        Assert.Equal("first Monday", WeekReader.At(week, at.AddDays(14), Berlin)?.Entry?.Name);
    }

    [Fact]
    public void WhichWeekOfTheCycleIsOn_DoesNotDependOnWhenYouAsk()
    {
        /*
            Anchored to a fixed Monday rather than to "now", which is the whole reason
            CycleStart exists. If it were relative, a server restarted on the other Monday
            would swap the two weeks over, and the guide and playback could disagree with each
            other inside one session.
        */
        var monday = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Unspecified);

        var fromMonday = WeekReader.CycleStart(monday.AddHours(9), 2);
        var fromThursday = WeekReader.CycleStart(monday.AddDays(3), 2);
        var fromNextTuesday = WeekReader.CycleStart(monday.AddDays(8), 2);

        Assert.Equal(fromMonday, fromThursday);
        Assert.Equal(fromMonday, fromNextTuesday);

        // And the cycle after that one starts a fortnight on, not a week.
        Assert.Equal(fromMonday.AddDays(14), WeekReader.CycleStart(monday.AddDays(15), 2));
    }

    [Fact]
    public void OneWeekIsExactlyWhatItAlwaysWas()
    {
        // Every channel that exists is Weeks = 1, and a missing value in a stored file reads as
        // one. None of them may move by a second.
        var monday = new DateTime(2026, 8, 31, 9, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(WeekReader.WeekStart(monday), WeekReader.CycleStart(monday, 1));
        Assert.Equal(WeekReader.WeekStart(monday), WeekReader.CycleStart(monday, 0));
    }

    [Fact]
    public void AGapRunsToTheEndOfTheCycle_NotTheEndOfTheFirstWeek()
    {
        // Gaps close the loop by pointing the last row at the first one "a cycle later". With
        // the old constant the fortnight's only gap would have come out negative and vanished,
        // and a schedule with one programme in it would have read as dark air nowhere.
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 2 };
        week.Airings.Add(Row(0, Hour, "only"));

        var gaps = WeekEditing.Gaps(week.Airings, week.CycleSeconds);

        Assert.Single(gaps);
        Assert.Equal(Hour, gaps[0].StartSecond);
        Assert.Equal((2 * Week) - Hour, gaps[0].DurationSeconds);
    }

    [Fact]
    public void PlacingInTheSecondWeek_LeavesTheFirstAlone()
    {
        // Same time, other week: an appointment must not trim its opposite number.
        var week = Fortnight();
        var placed = Row(Week + (20 * Hour), Hour, "replacement");

        var after = WeekEditing.Place(week.Airings, placed, week.CycleSeconds);

        Assert.Contains(after, a => a.Name == "first Monday" && a.DurationSeconds == 2 * Hour);
        Assert.Contains(after, a => a.Name == "replacement");
    }

    [Fact]
    public void ShrinkingBackToOneWeek_KeepsTheFirstWeekAndDropsTheRest()
    {
        // Wrapping week two onto week one would be a schedule nobody asked for. "Make it one
        // week again" means keep the first one.
        var week = Fortnight();

        var after = LiteTvController.RunEdits(
            week,
            week.ChannelId,
            new[] { new WeekEditDto { Kind = "Length", Weeks = 1 } },
            _ => new StoredWeek(),
            _ => 1800);

        Assert.NotNull(after);
        Assert.Equal(1, after!.Weeks);
        Assert.Single(after.Airings);
        Assert.Equal("first Monday", after.Airings[0].Name);
    }

    [Fact]
    public void GrowingToAFortnight_KeepsEverythingAndAddsEmptyTime()
    {
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 };
        week.Airings.Add(Row(20 * Hour, 2 * Hour, "Monday film"));

        var after = LiteTvController.RunEdits(
            week,
            week.ChannelId,
            new[] { new WeekEditDto { Kind = "Length", Weeks = 2 } },
            _ => new StoredWeek(),
            _ => 1800);

        Assert.Equal(2, after!.Weeks);
        Assert.Single(after.Airings);
        Assert.Equal(20 * Hour, after.Airings[0].StartSecond);
    }

    [Fact]
    public void TheLengthIsCapped_RatherThanTakenOnTrust()
    {
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 };

        var silly = LiteTvController.RunEdits(
            week, week.ChannelId,
            new[] { new WeekEditDto { Kind = "Length", Weeks = 5000 } },
            _ => new StoredWeek(), _ => 1800);
        Assert.Equal(LiteTvController.MaximumWeeks, silly!.Weeks);

        var none = LiteTvController.RunEdits(
            week, week.ChannelId,
            new[] { new WeekEditDto { Kind = "Length", Weeks = 0 } },
            _ => new StoredWeek(), _ => 1800);
        Assert.Equal(1, none!.Weeks);
    }

    [Fact]
    public void LayingOutAgain_KeepsTheLength()
    {
        // Otherwise pressing "lay this week out" would quietly turn a fortnight back into a
        // week, which is a setting being undone by a button that says nothing about it.
        var week = Fortnight();

        var after = LiteTvController.RunEdits(
            week,
            week.ChannelId,
            new[] { new WeekEditDto { Kind = "Generate" } },
            weeks => new StoredWeek { ChannelId = week.ChannelId, Weeks = weeks },
            _ => 1800);

        Assert.Equal(2, after!.Weeks);
    }

    [Fact]
    public void ARowInTheSecondWeek_SurvivesBeingWrittenAndReadBack()
    {
        // The only thing that makes a longer schedule real: the second week's rows keep their
        // place through Normalise, which every stored week goes through.
        var week = Fortnight();
        var normalised = WeekEditing.Normalise(week.Airings, week.CycleSeconds);

        Assert.Equal(2, normalised.Count);
        Assert.Contains(normalised, a => a.StartSecond == Week + (20 * Hour));
    }
}
