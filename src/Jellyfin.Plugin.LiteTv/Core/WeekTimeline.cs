namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// One block's claim on the week: when it starts, how long it runs, and which local
/// weekdays it starts on.
/// </summary>
/// <param name="Owner">The lineup the block airs; see <see cref="WeekTimeline.BaseLineup"/>.</param>
/// <param name="StartMinutes">Minutes after local midnight the block starts at.</param>
/// <param name="DurationMinutes">How long the block runs; may reach past midnight.</param>
/// <param name="Days">The local weekdays the block starts on; empty means every day.</param>
public sealed record BlockWindow(int Owner, int StartMinutes, int DurationMinutes, IReadOnlyList<DayOfWeek> Days);

/// <summary>
/// A span of the week owned by one lineup.
/// </summary>
/// <param name="Owner">The lineup airing during the span.</param>
/// <param name="StartMinute">The minute of the week the span starts at (inclusive).</param>
/// <param name="EndMinute">The minute of the week the span ends at (exclusive).</param>
public sealed record TimelineSpan(int Owner, int StartMinute, int EndMinute);

/// <summary>
/// Which lineup a channel airs at any point of the week.
/// <para>
/// The week is the unit because everything a channel's programming repeats on repeats
/// weekly: "weekday mornings", "Saturday evening", "every day at 20:15" are all patterns
/// with a period of one week. Resolving the week once turns every later question - what is
/// on now, what is on at 21:00 next Tuesday, how much has this block aired since the anchor
/// - into arithmetic on a fixed table, with no need to walk forward day by day from the
/// anchor. That matters: the anchor can be years back.
/// </para>
/// <para>
/// Blocks are laid down in configuration order and each one only takes the minutes still
/// free, so overlapping blocks resolve to the first one that asked. Everything left over
/// belongs to the base lineup, which is what a channel with no blocks at all is: one span
/// covering the whole week.
/// </para>
/// <para>
/// All of it is local wall-clock time, so the blocks stay where the viewer put them across
/// a daylight-saving change. The hour the clocks move is simply aired twice or not at all,
/// which is exactly what happens to broadcast schedules.
/// </para>
/// </summary>
public sealed class WeekTimeline
{
    /// <summary>The lineup that airs whatever no block claims: the channel's own sources.</summary>
    public const int BaseLineup = -1;

    /// <summary>Minutes in a day.</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>Minutes in a week.</summary>
    public const int MinutesPerWeek = 7 * MinutesPerDay;

    private readonly List<TimelineSpan> _spans;
    private readonly Dictionary<int, int> _weeklyMinutes = new();

    private WeekTimeline(List<TimelineSpan> spans)
    {
        _spans = spans;
        foreach (var span in spans)
        {
            _weeklyMinutes.TryGetValue(span.Owner, out var minutes);
            _weeklyMinutes[span.Owner] = minutes + (span.EndMinute - span.StartMinute);
        }
    }

    /// <summary>
    /// Gets the spans of the week, in order, together covering it exactly once.
    /// </summary>
    public IReadOnlyList<TimelineSpan> Spans => _spans;

    /// <summary>
    /// Lays the blocks over the week.
    /// </summary>
    /// <param name="blocks">The blocks, in the order they take precedence.</param>
    /// <returns>The resolved week.</returns>
    public static WeekTimeline Build(IReadOnlyList<BlockWindow> blocks)
    {
        // A minute-per-cell week is 10080 cells: small enough that claiming minute by
        // minute is simpler than interval arithmetic and just as fast, and it makes
        // overlap, midnight-wrapping and week-wrapping fall out without a special case.
        var owners = new int[MinutesPerWeek];
        Array.Fill(owners, BaseLineup);

        foreach (var block in blocks)
        {
            if (block.DurationMinutes <= 0)
            {
                continue;
            }

            var days = block.Days.Count > 0 ? block.Days : AllDays;
            foreach (var day in days)
            {
                var start = (DayIndex(day) * MinutesPerDay) + block.StartMinutes;
                var length = Math.Min(block.DurationMinutes, MinutesPerWeek);
                for (var i = 0; i < length; i++)
                {
                    var minute = Modulo(start + i, MinutesPerWeek);
                    if (owners[minute] == BaseLineup)
                    {
                        owners[minute] = block.Owner;
                    }
                }
            }
        }

        var spans = new List<TimelineSpan>();
        var spanStart = 0;
        for (var minute = 1; minute <= MinutesPerWeek; minute++)
        {
            if (minute == MinutesPerWeek || owners[minute] != owners[spanStart])
            {
                spans.Add(new TimelineSpan(owners[spanStart], spanStart, minute));
                spanStart = minute;
            }
        }

        return new WeekTimeline(spans);
    }

    /// <summary>
    /// Gets the minute of the week a local time falls in, counting from Monday 00:00.
    /// </summary>
    /// <param name="local">The local time.</param>
    /// <returns>The absolute minute; take it modulo a week for the minute of the week.</returns>
    public static long AbsoluteMinute(DateTime local)
    {
        // Year 1 January 1st is a Monday, so absolute minutes taken from it are already
        // aligned to the week and the modulo below needs no correction.
        return local.Ticks / TimeSpan.TicksPerMinute;
    }

    /// <summary>
    /// Gets the lineup on air at an absolute minute.
    /// </summary>
    /// <param name="absoluteMinute">The absolute minute.</param>
    /// <returns>The lineup owner.</returns>
    public int OwnerAt(long absoluteMinute)
    {
        return SpanAt((int)Modulo(absoluteMinute, MinutesPerWeek)).Owner;
    }

    /// <summary>
    /// Gets the next absolute minute at which the lineup on air changes.
    /// </summary>
    /// <param name="absoluteMinute">The absolute minute to look forward from.</param>
    /// <returns>The next changeover, or null when the channel airs one lineup all week and
    /// so never changes over.</returns>
    public long? NextChangeAfter(long absoluteMinute)
    {
        if (_spans.Count == 1)
        {
            return null;
        }

        var weekStart = absoluteMinute - Modulo(absoluteMinute, MinutesPerWeek);
        var minuteOfWeek = (int)(absoluteMinute - weekStart);
        var span = SpanAt(minuteOfWeek);

        // The last span of the week runs into the first one when both air the same lineup,
        // in which case the changeover is where that merged span ends, a week on.
        if (span.EndMinute == MinutesPerWeek && _spans[0].Owner == span.Owner)
        {
            return weekStart + MinutesPerWeek + _spans[0].EndMinute;
        }

        return weekStart + span.EndMinute;
    }

    /// <summary>
    /// Gets the absolute minute the lineup currently on air came on at.
    /// </summary>
    /// <param name="absoluteMinute">The absolute minute to look back from.</param>
    /// <returns>When the current span started, or null when the channel airs one lineup all
    /// week and so has no start to point at.</returns>
    public long? SpanStartAt(long absoluteMinute)
    {
        if (_spans.Count == 1)
        {
            return null;
        }

        var weekStart = absoluteMinute - Modulo(absoluteMinute, MinutesPerWeek);
        var span = SpanAt((int)(absoluteMinute - weekStart));

        // A span that starts the week is a continuation of the one that ends it when both
        // air the same lineup: the block came on last week and has not changed over since.
        if (span.StartMinute == 0 && _spans[^1].Owner == span.Owner)
        {
            return weekStart - MinutesPerWeek + _spans[^1].StartMinute;
        }

        return weekStart + span.StartMinute;
    }

    /// <summary>
    /// Gets how many minutes a lineup has been on air up to an absolute minute, counting
    /// from the start of time rather than from any anchor: the difference between two of
    /// these is the airtime between them, which is what the schedule position is measured
    /// in. A lineup does not advance while another one is on air - a block picks up where
    /// it left off the last time it was on, rather than where the wall clock would have
    /// carried it.
    /// </summary>
    /// <param name="owner">The lineup.</param>
    /// <param name="absoluteMinute">The absolute minute to count up to.</param>
    /// <returns>The minutes aired.</returns>
    public long AirtimeMinutesUpTo(int owner, long absoluteMinute)
    {
        var weekly = _weeklyMinutes.TryGetValue(owner, out var value) ? value : 0;
        if (weekly == 0)
        {
            return 0;
        }

        var minuteOfWeek = Modulo(absoluteMinute, MinutesPerWeek);
        var weeks = (absoluteMinute - minuteOfWeek) / MinutesPerWeek;

        long withinWeek = 0;
        foreach (var span in _spans)
        {
            if (span.Owner != owner || span.StartMinute >= minuteOfWeek)
            {
                continue;
            }

            withinWeek += Math.Min(span.EndMinute, minuteOfWeek) - span.StartMinute;
        }

        return (weeks * weekly) + withinWeek;
    }

    /// <summary>
    /// Gets whether a lineup is ever on air.
    /// </summary>
    /// <param name="owner">The lineup.</param>
    /// <returns>True when the week gives it any time at all.</returns>
    public bool Airs(int owner) => _weeklyMinutes.ContainsKey(owner);

    private static readonly DayOfWeek[] AllDays =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    /// <summary>Monday is 0, because the week the timeline counts in starts there.</summary>
    private static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static long Modulo(long value, int period)
    {
        var result = value % period;
        return result < 0 ? result + period : result;
    }

    private TimelineSpan SpanAt(int minuteOfWeek)
    {
        // Spans are few - one per changeover in the week - so a scan beats an index.
        foreach (var span in _spans)
        {
            if (minuteOfWeek < span.EndMinute)
            {
                return span;
            }
        }

        return _spans[^1];
    }
}
