using Jellyfin.Plugin.LiteTv.Configuration;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Lays a channel's hand-made exceptions over the schedule its queue generated.
/// <para>
/// A LiteTV schedule is generated rather than stored: the anchor plus the queue decide what is
/// on at any instant, which is what makes it identical on every client, free to compute and
/// endless. Editing it therefore cannot mean editing a stored list, because there is none. It
/// means keeping a small set of exceptions - see <see cref="ScheduleEdit"/> - and laying them
/// over what the generator says, which is what this does.
/// </para>
/// <para>
/// Every edit is an appointment: it holds its own stretch of the clock, and everything the
/// generator had in that stretch bends around it - trimmed at either end, dropped when the edit
/// covers it entirely. That is what a broadcaster's schedule does around a fixture, and it is
/// the only rule that behaves sensibly whatever the edit's length turns out to be. Swapping one
/// programme for another in the same slot looks simpler and then has nothing honest to say when
/// the replacement is half an hour longer than what it replaced.
/// </para>
/// <para>
/// Pure, and separate from <see cref="ChannelGuide"/>, so that the arithmetic - which is where
/// an off-by-one hides for weeks - can be tested without a library, a server or a channel.
/// </para>
/// </summary>
public static class ScheduleEditing
{
    /// <summary>
    /// The least of a programme worth airing once an edit has taken a bite out of it. Below
    /// this the remainder is dropped: a minute of the end of a film is not viewing, and a guide
    /// full of slivers is unreadable.
    /// </summary>
    public static readonly TimeSpan MinimumRemainder = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Applies a channel's edits to a generated window.
    /// </summary>
    /// <param name="airings">What the generator says, in order.</param>
    /// <param name="edits">The channel's edits, in any order.</param>
    /// <param name="fromUtc">The window's start.</param>
    /// <param name="toUtc">The window's end.</param>
    /// <param name="runtimeOf">How long a library item runs, in ticks.</param>
    /// <param name="nameOf">What a library item is called.</param>
    /// <param name="channelName">The channel, for the block name an edit carries.</param>
    /// <returns>The window as it will actually air.</returns>
    public static IEnumerable<Airing> Apply(
        IEnumerable<Airing> airings,
        IReadOnlyList<ScheduleEdit> edits,
        DateTime fromUtc,
        DateTime toUtc,
        Func<Guid, long> runtimeOf,
        Func<Guid, string> nameOf,
        string channelName)
    {
        var appointments = edits
            .Where(e => e.Enabled)
            .Select(e => (Edit: e, Start: e.StartUtc, End: e.StartUtc + Length(e, runtimeOf)))
            .Where(e => e.End > e.Start && e.End > fromUtc && e.Start < toUtc)
            .OrderBy(e => e.Start)
            .ToList();

        if (appointments.Count == 0)
        {
            return airings;
        }

        var result = new List<Airing>();

        foreach (var airing in airings)
        {
            // Every piece of this airing that no edit has claimed. Usually one piece - the
            // whole of it - and occasionally two, when an edit lands in the middle.
            var cursor = airing.StartUtc;
            foreach (var (_, start, end) in appointments)
            {
                if (end <= cursor || start >= airing.EndUtc)
                {
                    continue;
                }

                if (start > cursor)
                {
                    Keep(result, airing, cursor, start);
                }

                if (end > cursor)
                {
                    cursor = end;
                }
            }

            if (cursor < airing.EndUtc)
            {
                Keep(result, airing, cursor, airing.EndUtc);
            }
        }

        foreach (var (edit, start, end) in appointments)
        {
            if (edit.Kind == ScheduleEditKind.Remove)
            {
                // Nothing airs at all: the hole is the point of it, and a channel with a hole
                // in it is off air for that stretch, which the guide already knows how to say.
                continue;
            }

            var entry = edit.ItemId != Guid.Empty
                ? new ScheduledEntry(
                    edit.ItemId,
                    string.IsNullOrWhiteSpace(edit.Name) ? nameOf(edit.ItemId) : edit.Name,
                    null,
                    null,
                    (end - start).Ticks)
                : null;

            result.Add(new Airing(
                entry is not null ? AiringKind.Program : AiringKind.Interstitial,
                entry,
                start,
                end,
                0,
                channelName,
                null)
            {
                TrailerUrl = entry is null && !string.IsNullOrWhiteSpace(edit.Url) ? edit.Url : null
            });
        }

        return result.OrderBy(a => a.StartUtc).ToList();
    }

    /// <summary>
    /// How long an edit occupies: its item's runtime, or the time it was given.
    /// </summary>
    /// <param name="edit">The edit.</param>
    /// <param name="runtimeOf">How long a library item runs, in ticks.</param>
    /// <returns>The length.</returns>
    public static TimeSpan Length(ScheduleEdit edit, Func<Guid, long> runtimeOf)
    {
        if (edit.ItemId != Guid.Empty && edit.Kind == ScheduleEditKind.Air)
        {
            var ticks = runtimeOf(edit.ItemId);
            if (ticks > 0)
            {
                return TimeSpan.FromTicks(ticks);
            }
        }

        return TimeSpan.FromSeconds(Math.Max(1, edit.DurationSeconds));
    }

    /// <summary>Keeps the part of an airing that survived the edits laid over it.</summary>
    private static void Keep(List<Airing> result, Airing airing, DateTime from, DateTime to)
    {
        if (to - from < MinimumRemainder)
        {
            return;
        }

        result.Add(airing with
        {
            StartUtc = from,
            EndUtc = to,

            // The offset moves with the cut. A programme joined after an advert has to start
            // where the advert left it, or the channel would replay the part it just skipped.
            OffsetTicks = airing.OffsetTicks + (from - airing.StartUtc).Ticks
        });
    }
}
