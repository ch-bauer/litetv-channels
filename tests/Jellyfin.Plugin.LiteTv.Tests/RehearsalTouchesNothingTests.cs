using System;
using System.Linq;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Asking what a run of edits would come to must leave the week it was asked about alone.
/// <para>
/// Found on the test server, 28 Aug 2026, and it is the more dangerous of the two faults that
/// day. <see cref="WeekStore.Get"/> hands back the <b>cached instance</b> - the very object the
/// guide and playback read - and <see cref="LiteTvController.RunEdits"/> walked it in place. So
/// a rehearsal, the one operation that promises to write nothing, changed what a channel was
/// airing: asking what a fortnight would look like left a real channel a fortnight, live, with
/// nothing on disk to show for it. A restart undid it, which is exactly why it could have gone
/// unnoticed for a long time.
/// </para>
/// <para>
/// Every earlier test missed it because they all handed <c>RunEdits</c> a week built for the
/// test and only looked at what came back. <b>Checking the return value proves the answer, not
/// the absence of a side effect.</b>
/// </para>
/// </summary>
public class RehearsalTouchesNothingTests
{
    private const int Hour = 3600;
    private const int Week = 7 * 24 * Hour;

    private static StoredWeek AWeek()
    {
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 };
        week.Airings.Add(new StoredAiring
        {
            Id = Guid.NewGuid(),
            StartSecond = 20 * Hour,
            DurationSeconds = 2 * Hour,
            Kind = StoredAiringKind.Programme,
            Name = "Monday film"
        });
        return week;
    }

    private static StoredWeek Rehearse(StoredWeek stored, params WeekEditDto[] edits)
        => LiteTvController.RunEdits(
            stored.Copy(),
            stored.ChannelId,
            edits,
            weeks => new StoredWeek { ChannelId = stored.ChannelId, Weeks = weeks },
            dto => dto.DurationSeconds > 0 ? dto.DurationSeconds : 1800)
            ?? new StoredWeek();

    [Fact]
    public void ChangingTheLength_DoesNotChangeTheWeekItWasAskedAbout()
    {
        // The one that was caught live: the answer said two weeks and so did the channel.
        var stored = AWeek();

        var answer = Rehearse(stored, new WeekEditDto { Kind = "Length", Weeks = 2 });

        Assert.Equal(2, answer.Weeks);
        Assert.Equal(1, stored.Weeks);
    }

    [Fact]
    public void PlacingSomething_DoesNotTrimTheStoredRows()
    {
        // Dropping half an hour into the middle of a two-hour film cuts it in two. If that
        // happens to the cached week, the channel is airing the cut version immediately.
        var stored = AWeek();
        var before = stored.Airings[0].DurationSeconds;

        var answer = Rehearse(stored, new WeekEditDto
        {
            Kind = "Place",
            Airing = new WeekAiringDto
            {
                Id = Guid.NewGuid(),
                StartSecond = 21 * Hour,
                DurationSeconds = 1800,
                Kind = "Programme",
                Name = "dropped"
            }
        });

        Assert.True(answer.Airings.Count > 1);
        Assert.Single(stored.Airings);
        Assert.Equal(before, stored.Airings[0].DurationSeconds);
    }

    [Fact]
    public void RemovingSomething_LeavesTheStoredWeekWithIt()
    {
        var stored = AWeek();
        var id = stored.Airings[0].Id;

        var answer = Rehearse(stored, new WeekEditDto { Kind = "Remove", AiringId = id });

        Assert.Empty(answer.Airings);
        Assert.Single(stored.Airings);
    }

    [Fact]
    public void ACopyKeepsEverythingWorthKeeping()
    {
        var stored = AWeek();
        stored.Weeks = 3;
        stored.Airings.Add(new StoredAiring
        {
            Id = Guid.NewGuid(),
            StartSecond = Week + (9 * Hour),
            DurationSeconds = 1800,
            Kind = StoredAiringKind.Advert,
            Url = "https://example.invalid/a",
            OffsetTicks = 42,
            Name = "advert"
        });

        var copy = stored.Copy();

        Assert.Equal(stored.Weeks, copy.Weeks);
        Assert.Equal(stored.ChannelId, copy.ChannelId);
        Assert.Equal(
            stored.Airings.Select(a => (a.Id, a.StartSecond, a.DurationSeconds, a.Kind, a.Url, a.OffsetTicks, a.Name)),
            copy.Airings.Select(a => (a.Id, a.StartSecond, a.DurationSeconds, a.Kind, a.Url, a.OffsetTicks, a.Name)));

        // And shares none of it.
        copy.Airings[0].DurationSeconds = 1;
        Assert.NotEqual(1, stored.Airings[0].DurationSeconds);
    }
}
