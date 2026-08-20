using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A cached schedule is reused only while the fingerprint matches, so anything that
/// changes what a channel airs has to change the fingerprint. Everything missed here is an
/// edit that silently does nothing until the cache ages out.
/// </summary>
public class ChannelFingerprintTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static TvChannel Channel()
    {
        return new TvChannel
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Test",
            AnchorUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            Sources = new List<ChannelSource> { new() { Type = ChannelSourceType.Movie, ItemId = ItemId } }
        };
    }

    private static ProgramBlock Block()
    {
        return new ProgramBlock
        {
            Name = "Block",
            StartMinutes = 1215,
            DurationMinutes = 180,
            Days = new List<DayOfWeek> { DayOfWeek.Saturday },
            Sources = new List<ChannelSource> { new() { Type = ChannelSourceType.Series, ItemId = ItemId } }
        };
    }

    [Fact]
    public void Fingerprint_UnchangedChannel_IsStable()
    {
        Assert.Equal(ChannelPlaylistBuilder.Fingerprint(Channel()), ChannelPlaylistBuilder.Fingerprint(Channel()));
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public void Fingerprint_AnyEditThatChangesTheSchedule_ChangesIt(string name, Action<TvChannel> edit)
    {
        Assert.NotNull(name);
        var edited = Channel();
        edit(edited);

        Assert.NotEqual(ChannelPlaylistBuilder.Fingerprint(Channel()), ChannelPlaylistBuilder.Fingerprint(edited));
    }

    public static TheoryData<string, Action<TvChannel>> Edits() => new()
    {
        { "play order", c => c.Order = PlayOrder.Shuffle },
        { "slot minutes", c => c.SlotMinutes = 30 },
        { "trailers in gaps", c => c.TrailersInGaps = false },
        { "trailer mode", c => c.Trailers = TrailerMode.Preview },
        { "trailer spacing", c => c.TrailerEveryPrograms = 5 },
        { "trailer lookahead", c => c.TrailerLookahead = 7 },
        { "a trailer title added", c => c.TrailerTitles.Add(new ChannelSource { Type = ChannelSourceType.Movie, ItemId = Guid.NewGuid() }) },
        { "episodes per block", c => c.EpisodesPerBlock = 2 },
        { "anchor", c => c.AnchorUtc = c.AnchorUtc.AddMinutes(1) },
        { "a source added", c => c.Sources.Add(new ChannelSource { Type = ChannelSourceType.Movie, ItemId = Guid.NewGuid() }) },
        { "a source removed", c => c.Sources.Clear() },
        { "source type", c => c.Sources[0].Type = ChannelSourceType.Series },
        { "a block added", c => c.Blocks.Add(Block()) },
        { "block start", c => { var b = Block(); b.StartMinutes = 600; c.Blocks.Add(b); } },
        { "block duration", c => { var b = Block(); b.DurationMinutes = 60; c.Blocks.Add(b); } },
        { "block weekdays", c => { var b = Block(); b.Days = new List<DayOfWeek> { DayOfWeek.Monday }; c.Blocks.Add(b); } },
        { "block switched off", c => { var b = Block(); b.Enabled = false; c.Blocks.Add(b); } },
        { "block order", c => { var b = Block(); b.Order = PlayOrder.Shuffle; c.Blocks.Add(b); } },
        { "block sources", c => { var b = Block(); b.Sources.Clear(); c.Blocks.Add(b); } },
    };

    [Fact]
    public void Fingerprint_TwoBlocksSwapped_Differs()
    {
        var first = Channel();
        var a = Block();
        var b = Block();
        b.Name = "Second";
        b.StartMinutes = 600;
        first.Blocks.Add(a);
        first.Blocks.Add(b);

        var swapped = Channel();
        swapped.Blocks.Add(b);
        swapped.Blocks.Add(a);

        // Order decides which block wins an overlap, so it is part of what the channel airs.
        Assert.NotEqual(ChannelPlaylistBuilder.Fingerprint(first), ChannelPlaylistBuilder.Fingerprint(swapped));
    }
}
