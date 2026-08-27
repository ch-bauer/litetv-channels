namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// The rules for changing a stored week: what happens to everything else when something is
/// dropped onto the timeline.
/// <para>
/// One rule, the same one a broadcaster uses and the same one the old override layer used:
/// <b>what you place is an appointment.</b> It holds its stretch of the week and everything
/// already there bends around it - trimmed at either end, cut in two when the appointment
/// lands in the middle, dropped when it is covered entirely. That is the only rule that still
/// behaves sensibly when the thing dropped is half an hour longer than the thing it landed on,
/// which "swap one for the other" has nothing honest to say about.
/// </para>
/// <para>
/// The week is a loop, so all of this is arithmetic on a circle: a row may start on Sunday
/// evening and run into Monday morning, and an appointment dropped across midnight on Sunday
/// trims the row that starts the week just as readily as the one that ends it.
/// </para>
/// <para>
/// Pure, and free of storage, a library and a clock, so the arithmetic - which is where an
/// off-by-one hides for weeks - can be tested on its own.
/// </para>
/// </summary>
public static class WeekEditing
{
    /// <summary>
    /// The least of something worth keeping once an appointment has taken a bite out of it.
    /// Fifteen seconds rather than the two minutes the old override layer used, because a
    /// week holds adverts and idents that are legitimately half a minute long, and a rule
    /// written for feature films would swallow them.
    /// </summary>
    public const int MinimumRemainderSeconds = 15;

    /// <summary>
    /// Puts something on the timeline, bending everything else around it.
    /// </summary>
    /// <param name="airings">What the week holds now.</param>
    /// <param name="placed">What is being placed; anything already there with the same id is
    /// replaced rather than trimmed, which is what makes dragging a row a move rather than a
    /// copy that eats its own original.</param>
    /// <returns>The week as it now stands, in order.</returns>
    public static List<StoredAiring> Place(IEnumerable<StoredAiring> airings, StoredAiring placed)
        => Place(airings, placed, StoredWeek.SecondsPerWeek);

    /// <summary>
    /// Puts something on the timeline of a schedule that may be longer than a week.
    /// </summary>
    /// <param name="airings">What the schedule holds now.</param>
    /// <param name="placed">What is being placed.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>The schedule as it now stands, in order.</returns>
    public static List<StoredAiring> Place(IEnumerable<StoredAiring> airings, StoredAiring placed, int cycleSeconds)
    {
        var appointment = Clamp(placed, cycleSeconds);
        var result = new List<StoredAiring>();

        foreach (var existing in airings)
        {
            if (existing.Id == appointment.Id)
            {
                continue;
            }

            result.AddRange(Subtract(existing, appointment.StartSecond, appointment.DurationSeconds, cycleSeconds));
        }

        result.Add(appointment);
        return Sorted(result);
    }

    /// <summary>
    /// Takes something off the timeline. What it occupied becomes a gap; nothing slides up to
    /// fill it, because a week is a clock rather than a queue and the programme at nine is
    /// still at nine.
    /// </summary>
    /// <param name="airings">What the week holds now.</param>
    /// <param name="id">The row to remove.</param>
    /// <returns>The week without it.</returns>
    public static List<StoredAiring> Remove(IEnumerable<StoredAiring> airings, Guid id)
        => Sorted(airings.Where(a => a.Id != id).ToList());

    /// <summary>
    /// Puts a freshly generated or hand-assembled set of rows into the shape everything else
    /// here assumes: inside the week, long enough to matter, in order, and not overlapping.
    /// Later rows win over earlier ones, so a list can simply be laid down in the order it was
    /// meant to air.
    /// </summary>
    /// <param name="airings">The rows.</param>
    /// <returns>The normalised week.</returns>
    public static List<StoredAiring> Normalise(IEnumerable<StoredAiring> airings)
        => Normalise(airings, StoredWeek.SecondsPerWeek);

    /// <summary>
    /// The same, for a schedule that runs for more than one week before it repeats.
    /// </summary>
    /// <param name="airings">The rows.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>The normalised schedule.</returns>
    public static List<StoredAiring> Normalise(IEnumerable<StoredAiring> airings, int cycleSeconds)
    {
        var result = new List<StoredAiring>();
        foreach (var airing in airings)
        {
            var next = Clamp(airing, cycleSeconds);
            if (next.DurationSeconds < MinimumRemainderSeconds)
            {
                continue;
            }

            result = Place(result, next, cycleSeconds);
        }

        return Sorted(result);
    }

    /// <summary>
    /// The stretches of the week nothing claims, in order. These are what the guide shows as a
    /// break or as dark air, and what the timeline draws as empty.
    /// </summary>
    /// <param name="airings">A normalised week.</param>
    /// <returns>The gaps, as (start, duration) pairs in seconds of the week.</returns>
    public static List<(int StartSecond, int DurationSeconds)> Gaps(IReadOnlyList<StoredAiring> airings)
        => Gaps(airings, StoredWeek.SecondsPerWeek);

    /// <summary>
    /// The stretches of a schedule nothing claims, in order.
    /// </summary>
    /// <param name="airings">A normalised schedule.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>The gaps, as (start, duration) pairs in seconds of the cycle.</returns>
    public static List<(int StartSecond, int DurationSeconds)> Gaps(IReadOnlyList<StoredAiring> airings, int cycleSeconds)
    {
        var gaps = new List<(int, int)>();
        if (airings.Count == 0)
        {
            gaps.Add((0, cycleSeconds));
            return gaps;
        }

        var ordered = Sorted(airings.ToList());
        for (var i = 0; i < ordered.Count; i++)
        {
            // What follows the last row is the first one, a week on: the week is a loop, and
            // the stretch from Sunday night to Monday morning is one gap rather than two.
            var end = ordered[i].EndSecond;
            var nextStart = i + 1 < ordered.Count
                ? ordered[i + 1].StartSecond
                : ordered[0].StartSecond + cycleSeconds;

            var length = nextStart - end;
            if (length >= MinimumRemainderSeconds)
            {
                gaps.Add((Modulo(end, cycleSeconds), length));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Everything left of a row once an appointment has taken its stretch: nothing, the row
    /// itself, one trimmed end, or two pieces with a hole between them.
    /// </summary>
    /// <param name="existing">The row.</param>
    /// <param name="appointmentStart">Where the appointment starts, in seconds of the week.</param>
    /// <param name="appointmentLength">How long it runs.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>What survives.</returns>
    private static IEnumerable<StoredAiring> Subtract(StoredAiring existing, int appointmentStart, int appointmentLength, int cycleSeconds)
    {
        // Everything in the row's own frame: it begins at zero and runs to its length, so the
        // circle only has to be dealt with once, when the appointment is mapped into it.
        var length = existing.DurationSeconds;
        var offset = Modulo(appointmentStart - existing.StartSecond, cycleSeconds);

        // The appointment as this row sees it, and again a cycle earlier: an appointment
        // starting late in the cycle reaches a row that starts early in it by wrapping round,
        // and in the row's frame that is a stretch beginning at a negative number.
        var cuts = new[]
        {
            (From: offset, To: offset + appointmentLength),
            (From: offset - cycleSeconds, To: offset - cycleSeconds + appointmentLength)
        };

        var cursor = 0;
        foreach (var (from, to) in cuts.OrderBy(c => c.From))
        {
            if (to <= cursor || from >= length)
            {
                continue;
            }

            if (from > cursor)
            {
                var piece = Piece(existing, cursor, from, cycleSeconds);
                if (piece is not null)
                {
                    yield return piece;
                }
            }

            cursor = Math.Max(cursor, to);
        }

        if (cursor < length)
        {
            var tail = Piece(existing, cursor, length, cycleSeconds);
            if (tail is not null)
            {
                yield return tail;
            }
        }
    }

    /// <summary>
    /// One surviving piece of a row, measured in the row's own frame.
    /// </summary>
    /// <param name="existing">The row.</param>
    /// <param name="from">Where the piece starts, in seconds from the row's start.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>The piece, or null when it is too short to be worth airing.</returns>
    private static StoredAiring? Piece(StoredAiring existing, int from, int to, int cycleSeconds)
    {
        var duration = to - from;
        if (duration < MinimumRemainderSeconds)
        {
            return null;
        }

        return new StoredAiring
        {
            // A piece of a row is a new row. Keeping the id would give the week two rows
            // claiming to be the same one, and the page addresses rows by id.
            Id = from == 0 ? existing.Id : Guid.NewGuid(),
            StartSecond = Modulo(existing.StartSecond + from, cycleSeconds),
            DurationSeconds = duration,
            Kind = existing.Kind,
            ItemId = existing.ItemId,
            Name = existing.Name,
            Url = existing.Url,

            // The offset moves with the cut, or the second half of a film replays the part
            // the thing dropped on top of it just covered.
            OffsetTicks = existing.OffsetTicks + (from * TimeSpan.TicksPerSecond),
            SeriesName = existing.SeriesName,
            SeriesId = existing.SeriesId,
            BlockName = existing.BlockName,
            TrailedItemId = existing.TrailedItemId,
            TrailedName = existing.TrailedName
        };
    }

    /// <summary>
    /// A row with its start inside the cycle and its length no longer than one, which
    /// everything else here assumes and a request from a page does not promise.
    /// </summary>
    /// <param name="airing">The row.</param>
    /// <param name="cycleSeconds">How long the whole schedule is before it repeats.</param>
    /// <returns>The row, clamped.</returns>
    private static StoredAiring Clamp(StoredAiring airing, int cycleSeconds)
    {
        airing.StartSecond = Modulo(airing.StartSecond, cycleSeconds);
        airing.DurationSeconds = Math.Clamp(airing.DurationSeconds, 0, cycleSeconds);
        return airing;
    }

    private static List<StoredAiring> Sorted(List<StoredAiring> airings)
        => airings.OrderBy(a => a.StartSecond).ThenBy(a => a.DurationSeconds).ToList();

    private static int Modulo(int value, int period)
    {
        var result = value % period;
        return result < 0 ? result + period : result;
    }
}
