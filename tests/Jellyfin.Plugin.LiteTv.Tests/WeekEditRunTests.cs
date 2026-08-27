using System;
using System.Linq;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A run of schedule edits, folded over a stored week.
/// <para>
/// This is what the configuration page's Save now writes, and what it draws before you press
/// it: the same run, applied twice - once as a rehearsal and once for good. So the fold has to
/// give the same answer both times and has to apply in order, or the week somebody was shown is
/// not the week they get, which is worse than the immediate writes it replaced.
/// </para>
/// </summary>
public class WeekEditRunTests
{
    private const int Hour = 3600;

    private static readonly Guid Channel = Guid.NewGuid();

    private static WeekEditDto Place(Guid? id, int startSecond, int seconds, string name = "A")
        => new()
        {
            Kind = "Place",
            Airing = new WeekAiringDto
            {
                Id = id,
                StartSecond = startSecond,
                DurationSeconds = seconds,
                Kind = "Programme",
                Name = name
            }
        };

    private static WeekEditDto Remove(Guid id) => new() { Kind = "Remove", AiringId = id };

    private static StoredWeek Run(StoredWeek? from, params WeekEditDto[] edits)
        => LiteTvController.RunEdits(
            from,
            Channel,
            edits,
            weeks => new StoredWeek { ChannelId = Channel, Weeks = weeks },
            dto => dto.DurationSeconds > 0 ? dto.DurationSeconds : 1800)
            ?? new StoredWeek { ChannelId = Channel };

    [Fact]
    public void AnEmptyRun_LeavesTheWeekExactlyAsItWas()
    {
        // What the page asks for when the last pending edit is undone. It must not be a way to
        // quietly rewrite a curated week.
        var week = new StoredWeek { ChannelId = Channel };
        week.Airings = WeekEditing.Place(week.Airings, Row(20 * Hour, 2700));

        var after = Run(week);

        Assert.Single(after.Airings);
        Assert.Equal(20 * Hour, after.Airings[0].StartSecond);
    }

    [Fact]
    public void EditsApplyInOrder_AndTheLastOneWins()
    {
        var id = Guid.NewGuid();
        var after = Run(null, Place(id, 20 * Hour, 2700), Place(id, 21 * Hour, 2700));

        // A move is a Place with the id the week already holds, so two of them are one row that
        // ended up at nine, not two rows.
        Assert.Single(after.Airings);
        Assert.Equal(21 * Hour, after.Airings[0].StartSecond);
    }

    [Fact]
    public void PlacingThenRemoving_LeavesNothing()
    {
        var id = Guid.NewGuid();
        var after = Run(null, Place(id, 20 * Hour, 2700), Remove(id));
        Assert.Empty(after.Airings);
    }

    [Fact]
    public void ClearThrowsTheWeekAway_AndAPlaceAfterItStartsAFreshOne()
    {
        var week = new StoredWeek { ChannelId = Channel };
        week.Airings = WeekEditing.Place(week.Airings, Row(20 * Hour, 2700));

        var after = Run(week, new WeekEditDto { Kind = "Clear" }, Place(null, 9 * Hour, 1800, "B"));

        Assert.Single(after.Airings);
        Assert.Equal("B", after.Airings[0].Name);
    }

    [Fact]
    public void ClearOnItsOwn_LeavesNoWeekAtAll()
    {
        // Null is a real answer and not the same as an empty week: a channel with no stored
        // week airs from its sources, and the caller deletes the file rather than saving one.
        var week = new StoredWeek { ChannelId = Channel };
        week.Airings = WeekEditing.Place(week.Airings, Row(20 * Hour, 2700));

        var after = LiteTvController.RunEdits(
            week, Channel, new[] { new WeekEditDto { Kind = "Clear" } },
            weeks => new StoredWeek { ChannelId = Channel, Weeks = weeks },
            _ => 1800);

        Assert.Null(after);
    }

    [Fact]
    public void AnUnknownKind_IsSkippedRatherThanFailingTheRun()
    {
        // A page one release ahead of the server must not be able to lose the rest of a run.
        var id = Guid.NewGuid();
        var after = Run(null, new WeekEditDto { Kind = "Rotate" }, Place(id, 20 * Hour, 2700));
        Assert.Single(after.Airings);
    }

    [Fact]
    public void APlaceWithNoLength_GetsOneFromTheCaller()
    {
        // The page sends nothing on purpose; the length is the server's to work out. Storing
        // the zero is how a dropped programme became a hairline nobody could see.
        var after = Run(null, Place(null, 20 * Hour, 0));
        Assert.Equal(1800, after.Airings[0].DurationSeconds);
    }

    [Fact]
    public void TheSameRunTwice_GivesTheSameWeek()
    {
        // The rehearsal and the commit are two applications of one list. If they could differ,
        // the page would be showing a week that Save does not store.
        var edits = new[]
        {
            Place(Guid.NewGuid(), 20 * Hour, 2700, "A"),
            Place(Guid.NewGuid(), 20 * Hour + 1800, 2700, "B"),
        };

        var first = Run(null, edits);
        var second = Run(null, edits);

        Assert.Equal(
            first.Airings.Select(a => (a.Name, a.StartSecond, a.DurationSeconds)),
            second.Airings.Select(a => (a.Name, a.StartSecond, a.DurationSeconds)));
    }

    [Fact]
    public void APlacedRowStillBendsTheOnesAroundIt()
    {
        // The whole reason the rehearsal comes from the server rather than being drawn by the
        // page. A two-hour film with half an hour dropped into the middle of it is not trimmed,
        // it is CUT IN TWO - and no page is going to work that out for itself.
        var week = new StoredWeek { ChannelId = Channel };
        week.Airings = WeekEditing.Place(week.Airings, Row(20 * Hour, 2 * Hour, "long"));

        var after = Run(week, Place(Guid.NewGuid(), 21 * Hour, 1800, "dropped"));

        var pieces = after.Airings.Where(a => a.Name == "long").OrderBy(a => a.StartSecond).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal((20 * Hour, Hour), (pieces[0].StartSecond, pieces[0].DurationSeconds));
        Assert.Equal((21 * Hour + 1800, 1800), (pieces[1].StartSecond, pieces[1].DurationSeconds));
        Assert.Contains(after.Airings, a => a.Name == "dropped");
    }

    private static StoredAiring Row(int startSecond, int seconds, string name = "A")
        => new()
        {
            Id = Guid.NewGuid(),
            StartSecond = startSecond,
            DurationSeconds = seconds,
            Kind = StoredAiringKind.Programme,
            Name = name
        };
}
