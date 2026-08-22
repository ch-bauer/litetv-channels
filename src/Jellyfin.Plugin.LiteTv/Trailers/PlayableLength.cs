using System.Globalization;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// How long a linked trailer or advert actually occupies a break: its length, less the parts
/// the player is going to skip over.
/// <para>
/// The schedule used to be told this by hand - a "seconds" box beside the address, typed by
/// whoever added it. That number was wrong twice over. It was a guess at the video's length,
/// and it took no account of SponsorBlock, which on a trailer routinely removes ten or twenty
/// seconds of somebody's branded top and tail. A break sized by the guess and played with the
/// skips runs quiet at the end; sized short, it cuts the trailer off.
/// </para>
/// <para>
/// So the plugin works it out: the length comes from YouTube with the streams, the segments
/// come from SponsorBlock, and what is left is what the break has to hold. Pure, and separate
/// from either service, so the arithmetic can be tested without the network.
/// </para>
/// </summary>
public static class PlayableLength
{
    /// <summary>
    /// The shortest thing worth scheduling. Below this the answer is treated as unknown
    /// rather than as a very short trailer: a video whose segments cover nearly all of it is
    /// far more likely to be mis-marked than to be two seconds long.
    /// </summary>
    public const int MinimumSeconds = 3;

    /// <summary>
    /// Works out how much of a video is left once the skipped parts are taken out.
    /// </summary>
    /// <param name="lengthSeconds">The video's own length; zero or less means unknown.</param>
    /// <param name="segments">What the player will skip. Overlaps and segments reaching past
    /// the end of the video are handled - neither is unusual in community data.</param>
    /// <returns>The seconds that actually play, or zero when the length is unknown or what is
    /// left is too short to be believed.</returns>
    public static int Of(int lengthSeconds, IReadOnlyList<SponsorBlockClient.Segment>? segments)
    {
        if (lengthSeconds <= 0)
        {
            return 0;
        }

        var skipped = SkippedSeconds(lengthSeconds, segments);
        var playable = (int)Math.Round(lengthSeconds - skipped, MidpointRounding.AwayFromZero);

        return playable < MinimumSeconds ? 0 : playable;
    }

    /// <summary>
    /// How much of a video the player skips over, counting each second at most once.
    /// </summary>
    /// <param name="lengthSeconds">The video's length, which the total cannot exceed.</param>
    /// <param name="segments">The segments, in any order and possibly overlapping.</param>
    /// <returns>The seconds removed.</returns>
    public static double SkippedSeconds(int lengthSeconds, IReadOnlyList<SponsorBlockClient.Segment>? segments)
    {
        if (segments is null || segments.Count == 0 || lengthSeconds <= 0)
        {
            return 0;
        }

        // Clamped into the video and merged before anything is added up. The client merges
        // too, but this is also handed hand-written data and a segment counted twice is a
        // break that ends early - which is the exact failure this class exists to stop.
        var ranges = segments
            .Select(s => (Start: Math.Clamp(s.StartSeconds, 0, lengthSeconds), End: Math.Clamp(s.EndSeconds, 0, lengthSeconds)))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start)
            .ToList();

        double total = 0;
        double cursor = double.NegativeInfinity;
        double openedAt = 0;

        foreach (var (start, end) in ranges)
        {
            if (start > cursor)
            {
                total += cursor > double.NegativeInfinity ? cursor - openedAt : 0;
                openedAt = start;
                cursor = end;
                continue;
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor > double.NegativeInfinity)
        {
            total += cursor - openedAt;
        }

        return Math.Min(total, lengthSeconds);
    }

    /// <summary>
    /// A length as a clock, for the configuration page and the log.
    /// </summary>
    /// <param name="seconds">The length.</param>
    /// <returns>Something like "2:07", or a dash when the length is unknown.</returns>
    public static string Clock(int seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
