using System;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// How long a channel's schedule has to be so that it plays everything before repeating.
/// <para>
/// This is what a schedule longer than a week is for, and it took the owner saying so to make
/// it plain: a channel of every SpongeBob episode should air all of them and then start again.
/// A week-long schedule airs the first week's worth for ever and never reaches the rest,
/// however many episodes the channel is given - and nothing anywhere says so, because a week
/// of SpongeBob looks exactly right.
/// </para>
/// <para>
/// Typing a number works too, but nobody knows what the number is. The server does: it already
/// measures how long a channel takes to play everything once, for the line on the Content
/// screen that says so in words.
/// </para>
/// </summary>
public class FitToContentTests
{
    [Fact]
    public void ContentShorterThanAWeek_StaysAWeek()
    {
        // Four films. A week is already longer than it needs, and shortening below a week is
        // not something the schedule can express.
        Assert.Equal(1, LiteTvController.WeeksForCycle(TimeSpan.FromHours(8)));
        Assert.Equal(1, LiteTvController.WeeksForCycle(TimeSpan.FromDays(6.5)));
    }

    [Fact]
    public void ExactlyAWeek_IsAWeek()
    {
        Assert.Equal(1, LiteTvController.WeeksForCycle(TimeSpan.FromDays(7)));
    }

    [Fact]
    public void ContentIsRoundedUp_BecauseShortCutsTheTailOff()
    {
        // Eight days of episodes in a one-week schedule means the last day never airs. Nine
        // days needs two weeks, not one and a bit - a schedule is whole weeks.
        Assert.Equal(2, LiteTvController.WeeksForCycle(TimeSpan.FromDays(7.5)));
        Assert.Equal(2, LiteTvController.WeeksForCycle(TimeSpan.FromDays(14)));
        Assert.Equal(3, LiteTvController.WeeksForCycle(TimeSpan.FromDays(14.1)));
    }

    [Fact]
    public void AVeryLongChannel_GetsTheCap()
    {
        // Every SpongeBob episode is months of television. The cap airs far more of it than a
        // week ever did, and the alternative - a schedule of a year - is a stored file and a
        // timeline nobody can use.
        Assert.Equal(LiteTvController.MaximumWeeks, LiteTvController.WeeksForCycle(TimeSpan.FromDays(365)));
    }

    [Fact]
    public void NothingMeasurable_IsAWeek()
    {
        // A channel with no content, or one whose library gave no runtimes. One week is what
        // every channel was before this existed, so it is the answer that changes nothing.
        Assert.Equal(1, LiteTvController.WeeksForCycle(TimeSpan.Zero));
        Assert.Equal(1, LiteTvController.WeeksForCycle(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void FitLength_SetsTheLengthFromTheContent()
    {
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 };

        var after = LiteTvController.RunEdits(
            week,
            week.ChannelId,
            new[] { new WeekEditDto { Kind = "FitLength" } },
            weeks => new StoredWeek { Weeks = weeks },
            _ => 1800,
            () => 4);

        Assert.Equal(4, after!.Weeks);
    }

    [Fact]
    public void FitLengthThenGenerate_LaysOutOverTheFittedLength()
    {
        // The two are sent as one run, because fitting the length without laying the week out
        // again leaves the new weeks empty and the channel dark in them.
        var week = new StoredWeek { ChannelId = Guid.NewGuid(), Weeks = 1 };

        var after = LiteTvController.RunEdits(
            week,
            week.ChannelId,
            new[] { new WeekEditDto { Kind = "FitLength" }, new WeekEditDto { Kind = "Generate" } },
            weeks => new StoredWeek { ChannelId = week.ChannelId, Weeks = weeks },
            _ => 1800,
            () => 3);

        Assert.Equal(3, after!.Weeks);
    }
}
