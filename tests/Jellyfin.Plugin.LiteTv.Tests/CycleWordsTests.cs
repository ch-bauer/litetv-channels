using Jellyfin.Plugin.LiteTv.Api;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// How long a channel takes to play everything once, said in words.
/// <para>
/// The owner asked to be able to let a whole series run through and then start over, and noted
/// in passing how long that takes - months. The looping was never the missing part: a channel is
/// <c>airtime % TotalTicks</c> and has always played through and begun again. What could not be
/// seen was the scale of it, and a channel that repeats before the evening is out is a very
/// different thing from one that repeats at Christmas.
/// </para>
/// <para>
/// So the unit has to change with the length. These tests are mostly about that boundary
/// behaviour, because a cycle reported as "2928 hours" answers nobody's question.
/// </para>
/// </summary>
public class CycleWordsTests
{
    [Fact]
    public void NothingToPlayIsSaidPlainly()
    {
        Assert.Equal("Nothing to play yet.", LiteTvController.CycleWords(TimeSpan.Zero, 0));

        // Entries with no length is the same practical answer, and must not divide by zero
        // into something confident and wrong.
        Assert.Equal("Nothing to play yet.", LiteTvController.CycleWords(TimeSpan.Zero, 12));
    }

    [Fact]
    public void AnEveningIsSaidInHours()
    {
        var words = LiteTvController.CycleWords(TimeSpan.FromHours(6.5), 4);

        Assert.Contains("6.5 hours", words, StringComparison.Ordinal);
        Assert.Contains("4 things", words, StringComparison.Ordinal);
        Assert.Contains("starts over", words, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortCyclesAreSaidInMinutes()
    {
        var words = LiteTvController.CycleWords(TimeSpan.FromMinutes(42), 1);

        Assert.Contains("42 minutes", words, StringComparison.Ordinal);
        // One thing is "1 thing", not "1 things".
        Assert.Contains("1 thing ", words, StringComparison.Ordinal);
    }

    [Fact]
    public void AFewDaysIsSaidInDays()
    {
        var words = LiteTvController.CycleWords(TimeSpan.FromDays(9), 60);

        Assert.Contains("9 days", words, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeSeriesIsSaidInMonths()
    {
        // The case the owner actually described: one long series, played right through.
        // 111 episodes of about 47 minutes is roughly 87 days.
        var length = TimeSpan.FromMinutes(111 * 47 * 24);

        var words = LiteTvController.CycleWords(length, 111);

        Assert.Contains("months", words, StringComparison.Ordinal);
        Assert.DoesNotContain("hours", words, StringComparison.Ordinal);
        Assert.DoesNotContain("days", words, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.5, "hours")]
    [InlineData(59, "days")]
    [InlineData(61, "months")]
    public void TheUnitChangesWithTheLength(double days, string expectedUnit)
    {
        var words = LiteTvController.CycleWords(TimeSpan.FromDays(days), 10);

        Assert.Contains(expectedUnit, words, StringComparison.Ordinal);
    }
}
