using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The stored week: dropping things onto it, and reading it back as a schedule.
/// <para>
/// All of it is arithmetic on a circle, which is where the off-by-ones live. A week that is
/// wrong by a second still looks entirely plausible in a timeline and airs the wrong minute of
/// the right film; a week whose wrap is wrong looks perfect from Monday to Saturday.
/// </para>
/// </summary>
public class StoredWeekTests
{
    private const int Hour = 3600;
    private const int Day = 24 * Hour;

    /// <summary>Berlin, because that is the clock the owner's schedule is written on and it
    /// changes twice a year, which is the interesting part.</summary>
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

    private static StoredAiring Row(int startSecond, int durationSeconds, string name = "A", StoredAiringKind kind = StoredAiringKind.Programme)
        => new()
        {
            StartSecond = startSecond,
            DurationSeconds = durationSeconds,
            Kind = kind,
            ItemId = Guid.NewGuid(),
            Name = name
        };

    [Fact]
    public void Place_OnEmptyTime_LeavesEverythingElseAlone()
    {
        var week = new List<StoredAiring> { Row(0, Hour, "A") };

        var result = WeekEditing.Place(week, Row(4 * Hour, Hour, "B"));

        Assert.Equal(2, result.Count);
        Assert.Equal(Hour, result[0].DurationSeconds);
    }

    /// <summary>The appointment rule: what is dropped wins, and what it lands on is trimmed.</summary>
    [Fact]
    public void Place_OverlappingTheEndOfSomething_TrimsIt()
    {
        var week = new List<StoredAiring> { Row(0, 2 * Hour, "A") };

        var result = WeekEditing.Place(week, Row(Hour, Hour, "B"));

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Name);
        Assert.Equal(0, result[0].StartSecond);
        Assert.Equal(Hour, result[0].DurationSeconds);
        Assert.Equal("B", result[1].Name);
    }

    /// <summary>
    /// Dropped into the middle, a film becomes two airings - and the second one resumes where
    /// the first left off rather than replaying the part the appointment covered.
    /// </summary>
    [Fact]
    public void Place_InTheMiddleOfSomething_CutsItInTwoAndMovesTheOffset()
    {
        var film = Row(0, 3 * Hour, "Film");

        var result = WeekEditing.Place(new List<StoredAiring> { film }, Row(Hour, Hour, "Break"));

        Assert.Equal(3, result.Count);

        var first = result[0];
        var second = result[2];
        Assert.Equal("Film", first.Name);
        Assert.Equal(0, first.StartSecond);
        Assert.Equal(Hour, first.DurationSeconds);
        Assert.Equal(0, first.OffsetTicks);

        Assert.Equal("Film", second.Name);
        Assert.Equal(2 * Hour, second.StartSecond);
        Assert.Equal(Hour, second.DurationSeconds);
        Assert.Equal(2L * Hour * TimeSpan.TicksPerSecond, second.OffsetTicks);
    }

    /// <summary>The two halves are two rows, and the page addresses rows by id.</summary>
    [Fact]
    public void Place_CuttingSomethingInTwo_GivesTheHalvesDifferentIds()
    {
        var film = Row(0, 3 * Hour, "Film");

        var result = WeekEditing.Place(new List<StoredAiring> { film }, Row(Hour, Hour, "Break"));

        Assert.Equal(3, result.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void Place_CoveringSomethingEntirely_DropsIt()
    {
        var week = new List<StoredAiring> { Row(Hour, Hour, "A") };

        var result = WeekEditing.Place(week, Row(0, 3 * Hour, "B"));

        Assert.Single(result);
        Assert.Equal("B", result[0].Name);
    }

    /// <summary>Moving a row is a move, not a copy that eats its own original.</summary>
    [Fact]
    public void Place_WithAnIdTheWeekAlreadyHolds_MovesItRatherThanDuplicatingIt()
    {
        var film = Row(0, Hour, "Film");
        var moved = Row(2 * Hour, Hour, "Film");
        moved.Id = film.Id;

        var result = WeekEditing.Place(new List<StoredAiring> { film }, moved);

        Assert.Single(result);
        Assert.Equal(2 * Hour, result[0].StartSecond);
    }

    /// <summary>
    /// The week is a loop. Something dropped across Sunday midnight trims what starts the
    /// week just as readily as what ends it - the case that looks perfect all week and is
    /// wrong once.
    /// </summary>
    [Fact]
    public void Place_AcrossTheEndOfTheWeek_TrimsWhatStartsIt()
    {
        var monday = Row(0, 2 * Hour, "Monday");

        var placed = Row(StoredWeek.SecondsPerWeek - Hour, 2 * Hour, "Overnight");
        var result = WeekEditing.Place(new List<StoredAiring> { monday }, placed);

        var survivor = result.Single(a => a.Name == "Monday");
        Assert.Equal(Hour, survivor.StartSecond);
        Assert.Equal(Hour, survivor.DurationSeconds);
        Assert.Equal((long)Hour * TimeSpan.TicksPerSecond, survivor.OffsetTicks);
    }

    /// <summary>A sliver left by a cut is not viewing, and a timeline full of them is unreadable.</summary>
    [Fact]
    public void Place_LeavingOnlyASliver_DropsIt()
    {
        var week = new List<StoredAiring> { Row(0, Hour, "A") };

        var result = WeekEditing.Place(week, Row(10, Hour, "B"));

        Assert.Single(result);
        Assert.Equal("B", result[0].Name);
    }

    [Fact]
    public void Remove_TakesOnlyThatRow_AndLeavesAHole()
    {
        var keep = Row(0, Hour, "A");
        var drop = Row(Hour, Hour, "B");

        var result = WeekEditing.Remove(new[] { keep, drop }, drop.Id);

        Assert.Single(result);
        Assert.Equal("A", result[0].Name);
    }

    [Fact]
    public void Gaps_OfAnEmptyWeek_IsTheWholeWeek()
    {
        var gaps = WeekEditing.Gaps(Array.Empty<StoredAiring>());

        Assert.Single(gaps);
        Assert.Equal(0, gaps[0].StartSecond);
        Assert.Equal(StoredWeek.SecondsPerWeek, gaps[0].DurationSeconds);
    }

    /// <summary>
    /// Sunday night to Monday morning is one hole, not two. Counting from zero rather than
    /// round the loop reports it as two and makes the week look like it goes dark twice.
    /// </summary>
    [Fact]
    public void Gaps_AcrossTheEndOfTheWeek_IsOneHole()
    {
        var rows = new List<StoredAiring> { Row(Hour, Hour, "A") };

        var gaps = WeekEditing.Gaps(rows);

        Assert.Single(gaps);
        Assert.Equal(2 * Hour, gaps[0].StartSecond);
        Assert.Equal(StoredWeek.SecondsPerWeek - Hour, gaps[0].DurationSeconds);
    }

    [Fact]
    public void Gaps_BetweenTwoRows_IsTheStretchBetweenThem()
    {
        var rows = new List<StoredAiring>
        {
            Row(0, Hour, "A"),
            Row(3 * Hour, Hour, "B")
        };

        var gaps = WeekEditing.Gaps(rows);

        var between = gaps.Single(g => g.StartSecond == Hour);
        Assert.Equal(2 * Hour, between.DurationSeconds);
    }

    /// <summary>
    /// A week read at a moment inside a programme reports that programme, and reports how far
    /// into it the viewer has joined - which is the whole point of a channel.
    /// </summary>
    [Fact]
    public void Reader_AtAMomentInsideAProgramme_ReportsItAndThePosition()
    {
        // Monday 20:00 local, for two hours.
        var week = WeekFrom(Row(20 * Hour, 2 * Hour, "Film"));

        // Monday 21:00 local, in a week whose Monday is the 5th of January 2026.
        var at = Utc(2026, 1, 5, 21, 0);

        var airing = WeekReader.At(week, at, Berlin);

        Assert.NotNull(airing);
        Assert.Equal(AiringKind.Program, airing!.Kind);
        Assert.Equal("Film", airing.Entry!.Name);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, airing.OffsetAt(at));
    }

    /// <summary>The week repeats forever: the same programme, the same time, next week.</summary>
    [Fact]
    public void Reader_AWeekLater_ReportsTheSameProgramme()
    {
        var week = WeekFrom(Row(20 * Hour, 2 * Hour, "Film"));

        var thisWeek = WeekReader.At(week, Utc(2026, 1, 5, 21, 0), Berlin);
        var nextWeek = WeekReader.At(week, Utc(2026, 1, 12, 21, 0), Berlin);

        Assert.Equal(thisWeek!.Entry!.Name, nextWeek!.Entry!.Name);
        Assert.Equal(thisWeek.OffsetAt(Utc(2026, 1, 5, 21, 0)), nextWeek.OffsetAt(Utc(2026, 1, 12, 21, 0)));
    }

    /// <summary>And backwards: there is no anchor to be on the wrong side of.</summary>
    [Fact]
    public void Reader_AYearEarlier_ReportsTheSameProgramme()
    {
        var week = WeekFrom(Row(20 * Hour, 2 * Hour, "Film"));

        var airing = WeekReader.At(week, Utc(2025, 1, 6, 21, 0), Berlin);

        Assert.Equal("Film", airing!.Entry!.Name);
    }

    /// <summary>
    /// A programme at 20:15 is at 20:15 in October as well as in July. The schedule is written
    /// on a wall clock, and the clock changing is not the schedule changing.
    /// </summary>
    [Fact]
    public void Reader_AcrossADaylightSavingChange_KeepsTheProgrammeAtTheSameClockTime()
    {
        var week = WeekFrom(Row((20 * Hour) + 900, 2 * Hour, "Film"));

        // Summer time in Berlin, and winter time: a Monday either side of the October change.
        var summer = WeekReader.At(week, Utc(2026, 7, 6, 21, 0), Berlin);
        var winter = WeekReader.At(week, Utc(2026, 11, 2, 21, 0), Berlin);

        Assert.Equal("Film", summer!.Entry!.Name);
        Assert.Equal("Film", winter!.Entry!.Name);
    }

    /// <summary>A hole of minutes is a break; a hole of hours is the channel being dark.</summary>
    [Fact]
    public void Reader_AShortHole_IsABreak_AndALongOne_IsOffAir()
    {
        var week = WeekFrom(
            Row(0, Hour, "A"),
            Row(Hour + 600, Hour, "B"),
            Row(6 * Hour, Hour, "C"));

        var shortHole = WeekReader.At(week, Utc(2026, 1, 5, 1, 5), Berlin);
        var longHole = WeekReader.At(week, Utc(2026, 1, 5, 4, 0), Berlin);

        Assert.Equal(AiringKind.Interstitial, shortHole!.Kind);
        Assert.Equal(AiringKind.OffAir, longHole!.Kind);
    }

    /// <summary>
    /// A break announces what it was built to announce, not whatever the week now happens to
    /// have put after it - the reason the choice is stored with the break at all.
    /// </summary>
    [Fact]
    public void Reader_ABreakThatNamesWhatItTrails_KeepsNamingIt()
    {
        var film = Row(4 * Hour, Hour, "Avatar");
        var advert = Row(600, 300, "Melitta", StoredAiringKind.Advert);
        advert.TrailedItemId = film.ItemId;
        advert.TrailedName = "Avatar";
        advert.Url = "https://example.invalid/advert";

        var week = WeekFrom(Row(0, 600, "Something"), advert, film);

        var airing = WeekReader.At(week, Utc(2026, 1, 5, 0, 11), Berlin);

        Assert.Equal(AiringKind.Interstitial, airing!.Kind);
        Assert.Equal("Avatar", airing.NextProgram!.Name);
        Assert.Equal("https://example.invalid/advert", airing.TrailerUrl);
    }

    /// <summary>
    /// The generator writes down one week of the computed schedule, and dark air is the
    /// absence of a row rather than a row.
    /// </summary>
    [Fact]
    public void Generator_WritesDownWhatAirs_AndNotWhatDoesNot()
    {
        var weekStart = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);
        var entry = new ScheduledEntry(Guid.NewGuid(), "Film", null, null, TimeSpan.FromHours(2).Ticks);

        var airings = new[]
        {
            new Airing(AiringKind.Program, entry, Utc(2026, 1, 5, 19, 0), Utc(2026, 1, 5, 21, 0), 0, null, null),
            new Airing(AiringKind.OffAir, null, Utc(2026, 1, 5, 21, 0), Utc(2026, 1, 6, 6, 0), 0, null, null)
        };

        var week = WeekGenerator.Build(Guid.NewGuid(), airings, weekStart, Berlin);

        var row = Assert.Single(week.Airings);
        Assert.Equal(StoredAiringKind.Programme, row.Kind);
        Assert.Equal(19 * Hour, row.StartSecond);
        Assert.Equal(2 * Hour, row.DurationSeconds);
    }

    /// <summary>
    /// An address in a break is an advert when it came from the channel's pool and a trailer
    /// when it did not; the schedule cannot tell them apart on its own, and the timeline draws
    /// them differently.
    /// </summary>
    [Fact]
    public void Generator_TellsAnAdvertFromATrailer_ByThePool()
    {
        var weekStart = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);

        var advert = new Airing(AiringKind.Interstitial, null, Utc(2026, 1, 5, 19, 0), Utc(2026, 1, 5, 19, 1), 0, null, null)
        {
            TrailerUrl = "https://example.invalid/advert"
        };
        var trailer = new Airing(AiringKind.Interstitial, null, Utc(2026, 1, 5, 19, 1), Utc(2026, 1, 5, 19, 3), 0, null, null)
        {
            TrailerUrl = "https://example.invalid/trailer"
        };

        var week = WeekGenerator.Build(
            Guid.NewGuid(),
            new[] { advert, trailer },
            weekStart,
            Berlin,
            new HashSet<string>(StringComparer.Ordinal) { "https://example.invalid/advert" });

        Assert.Equal(StoredAiringKind.Advert, week.Airings[0].Kind);
        Assert.Equal(StoredAiringKind.Trailer, week.Airings[1].Kind);
    }

    /// <summary>
    /// The week is the whole of the schedule, so what is still on air when Sunday ends is
    /// trimmed there rather than spilling into a second copy of Monday.
    /// </summary>
    [Fact]
    public void Generator_TrimsWhatRunsPastTheEndOfTheWeek()
    {
        var weekStart = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);
        var entry = new ScheduledEntry(Guid.NewGuid(), "Film", null, null, TimeSpan.FromHours(3).Ticks);

        // Sunday 23:00 local for three hours: one hour of it belongs to this week.
        var airing = new Airing(AiringKind.Program, entry, Utc(2026, 1, 11, 22, 0), Utc(2026, 1, 12, 1, 0), 0, null, null);

        var week = WeekGenerator.Build(Guid.NewGuid(), new[] { airing }, weekStart, Berlin);

        var row = Assert.Single(week.Airings);
        Assert.Equal((6 * Day) + (22 * Hour), row.StartSecond);
        Assert.Equal(2 * Hour, row.DurationSeconds);
    }

    private static StoredWeek WeekFrom(params StoredAiring[] rows)
        => new()
        {
            ChannelId = Guid.NewGuid(),
            Airings = WeekEditing.Normalise(rows)
        };

    /// <summary>A Berlin wall-clock time, as the UTC instant it happens at.</summary>
    private static DateTime Utc(int year, int month, int day, int hour, int minute)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            Berlin);
}
