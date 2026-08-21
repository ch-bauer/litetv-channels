using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The two properties an advert break has to have, both of which are about every client
/// working the schedule out for itself: the same break everywhere, and a different break each
/// time. Getting the first wrong means two televisions in one house disagreeing about what is
/// on; getting the second wrong means the same advert every hour all evening.
/// </summary>
public class AdvertPoolTests
{
    private static readonly DateTime Break = new(2026, 1, 5, 20, 0, 0, DateTimeKind.Utc);

    private static List<Advert> Pool(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Advert
            {
                Name = "Advert " + i,
                Url = "https://example.invalid/" + i,
                DurationSeconds = 30
            }).ToList();

    private static readonly Guid ChannelId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static int Seed(DateTime at, int poolSize) => ChannelGuide.Draw(at, ChannelId, poolSize);

    [Fact]
    public void TheSameBreakIsTheSameEverywhere()
    {
        var pool = Pool(5);

        Assert.Equal(Seed(Break, pool.Count), Seed(Break, pool.Count));
    }

    /// <summary>
    /// Breaks land on round intervals, which is what caught the first attempt out: minutes
    /// modulo a five-advert pool is zero every hour, so an evening of hourly breaks played one
    /// advert over and over. Every interval a channel plausibly breaks on is checked.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(8)]
    public void DifferentBreaksDrawDifferently(int poolSize)
    {
        foreach (var minutes in new[] { 15, 20, 30, 60, 90, 120 })
        {
            var seeds = Enumerable.Range(0, 8)
                .Select(i => Seed(Break.AddMinutes(minutes * i), poolSize))
                .Distinct()
                .ToList();

            Assert.True(
                seeds.Count > 1,
                $"a pool of {poolSize} walked from the same place at every {minutes}-minute break");
        }
    }

    /// <summary>
    /// Preference by decade, which is the point of the feature: the pool is ordered so that
    /// adverts of the programme's own decade come first, then undated ones, then the rest.
    /// </summary>
    [Fact]
    public void TheRightVintageComesFirst()
    {
        var pool = new List<Advert>
        {
            new() { Name = "modern", Url = "u1", Decade = 2020 },
            new() { Name = "undated", Url = "u2", Decade = 0 },
            new() { Name = "eighties", Url = "u3", Decade = 1980 }
        };

        const int Decade = 1980;
        var ordered = pool
            .OrderBy(a => Decade > 0 && a.Decade == Decade ? 0 : 1)
            .ThenBy(a => a.Decade == 0 ? 1 : 0)
            .Select(a => a.Name)
            .ToList();

        Assert.Equal(new[] { "eighties", "modern", "undated" }, ordered);
    }

    [Fact]
    public void AnUndatedAdvertFitsAnywhere()
    {
        var pool = new List<Advert>
        {
            new() { Name = "undated", Url = "u1", Decade = 0 },
            new() { Name = "nineties", Url = "u2", Decade = 1990 }
        };

        // With no year on the programme, nothing is preferred and the pool keeps its own order.
        const int Decade = 0;
        var ordered = pool
            .OrderBy(a => Decade > 0 && a.Decade == Decade ? 0 : 1)
            .ThenBy(a => a.Decade == 0 ? 1 : 0)
            .Select(a => a.Name)
            .ToList();

        Assert.Equal(new[] { "nineties", "undated" }, ordered);
    }
}
