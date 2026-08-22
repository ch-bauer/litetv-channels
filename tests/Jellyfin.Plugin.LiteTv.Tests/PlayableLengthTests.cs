using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// How long a linked trailer occupies a break once the skipped parts are taken out.
/// <para>
/// This replaced a number typed into a box, so it has to be right in the cases a person would
/// have got right by eye: overlapping community submissions, a segment that runs past the end
/// of the video, and a video that is nearly all sponsor. Each of those, counted wrong, is a
/// break that ends in silence or a trailer cut off mid-sentence.
/// </para>
/// </summary>
public class PlayableLengthTests
{
    private static SponsorBlockClient.Segment Seg(double from, double to, string category = "sponsor")
        => new(from, to, category);

    [Fact]
    public void WithNoSegments_IsTheWholeVideo()
    {
        Assert.Equal(127, PlayableLength.Of(127, Array.Empty<SponsorBlockClient.Segment>()));
        Assert.Equal(127, PlayableLength.Of(127, null));
    }

    /// <summary>The measured case: the Avatar trailer, with an 11.5 second outro.</summary>
    [Fact]
    public void WithOneSegment_TakesItOut()
    {
        var segments = new[] { Seg(136.6, 148.1, "outro") };

        Assert.Equal(137, PlayableLength.Of(148, segments));
    }

    [Fact]
    public void WithSeveralSegments_TakesThemAllOut()
    {
        var segments = new[] { Seg(0, 5), Seg(100, 110) };

        Assert.Equal(135, PlayableLength.Of(150, segments));
    }

    /// <summary>
    /// Overlapping submissions are ordinary in community data, and counting the overlap twice
    /// makes the break shorter than the trailer - which cuts it off.
    /// </summary>
    [Fact]
    public void WithOverlappingSegments_CountsTheOverlapOnce()
    {
        var segments = new[] { Seg(10, 30), Seg(20, 40) };

        Assert.Equal(30, PlayableLength.Of(60, segments));
        Assert.Equal(30, PlayableLength.SkippedSeconds(60, segments));
    }

    /// <summary>One segment wholly inside another is the same trap, spelled differently.</summary>
    [Fact]
    public void WithASegmentInsideAnother_CountsTheOuterOnce()
    {
        var segments = new[] { Seg(10, 40), Seg(15, 20) };

        Assert.Equal(30, PlayableLength.SkippedSeconds(60, segments));
    }

    /// <summary>Order is not promised by the caller, and must not change the answer.</summary>
    [Fact]
    public void WithSegmentsOutOfOrder_IsTheSameAnswer()
    {
        var forwards = new[] { Seg(0, 5), Seg(50, 60) };
        var backwards = new[] { Seg(50, 60), Seg(0, 5) };

        Assert.Equal(
            PlayableLength.SkippedSeconds(120, forwards),
            PlayableLength.SkippedSeconds(120, backwards));
    }

    [Fact]
    public void WithASegmentPastTheEnd_CountsOnlyWhatIsInTheVideo()
    {
        // The last ten seconds of a sixty-second video, not three hundred and fifty of them.
        var segments = new[] { Seg(50, 400) };

        Assert.Equal(10, PlayableLength.SkippedSeconds(60, segments));
        Assert.Equal(50, PlayableLength.Of(60, segments));
    }

    /// <summary>
    /// A video whose segments cover nearly all of it is far more likely to be mis-marked than
    /// to be two seconds long, and a two-second slot in a break is not worth scheduling.
    /// </summary>
    [Fact]
    public void WhenTheSkipsLeaveAlmostNothing_TheAnswerIsUnknown()
    {
        var segments = new[] { Seg(0, 59) };

        Assert.Equal(0, PlayableLength.Of(60, segments));
    }

    /// <summary>
    /// Unknown length has to stay unknown rather than becoming zero-that-looks-like-a-length:
    /// the caller falls back to its own reservation, and cannot do that if it is told "0 s".
    /// </summary>
    [Fact]
    public void WithNoLength_IsUnknownWhateverTheSegmentsSay()
    {
        Assert.Equal(0, PlayableLength.Of(0, new[] { Seg(0, 5) }));
        Assert.Equal(0, PlayableLength.SkippedSeconds(0, new[] { Seg(0, 5) }));
    }

    [Fact]
    public void Clock_ReadsAsATime()
    {
        Assert.Equal("2:07", PlayableLength.Clock(127));
        Assert.Equal("0:30", PlayableLength.Clock(30));
        Assert.Equal("—", PlayableLength.Clock(0));
    }
}
