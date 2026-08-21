using Jellyfin.Plugin.LiteTv.Configuration;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// The one place that answers "what is this channel airing, and when". Everything that
/// needs to know - the web guide, the published channel items, the Live TV service, the
/// session monitor - asks here, so they cannot disagree with each other about a schedule
/// they would otherwise each have resolved themselves.
/// </summary>
public sealed class ChannelGuide
{
    private readonly ChannelPlaylistBuilder _builder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGuide"/> class.
    /// </summary>
    /// <param name="builder">The playlist builder.</param>
    public ChannelGuide(ChannelPlaylistBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>
    /// Gets the channels that are on air, in configuration order.
    /// </summary>
    /// <returns>The enabled channels.</returns>
    public static IReadOnlyList<TvChannel> Channels()
        => Plugin.Instance?.Configuration.Channels.Where(c => c.Enabled).ToList() ?? new List<TvChannel>();

    /// <summary>
    /// Gets one channel by id, if it is on air.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <returns>The channel, or null when it is unknown or disabled.</returns>
    public static TvChannel? Channel(Guid channelId)
        => Channels().FirstOrDefault(c => c.Id == channelId);

    /// <summary>
    /// Gets what a channel is airing at one moment.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="utc">The moment; defaults to now.</param>
    /// <returns>The airing, or null when the channel has nothing to air at all.</returns>
    public Airing? NowOn(TvChannel channel, DateTime? utc = null)
    {
        var at = utc ?? DateTime.UtcNow;
        var schedule = _builder.GetSchedule(channel);
        if (schedule.IsSilent)
        {
            return null;
        }

        return Window(channel, at, at.AddMinutes(1)).FirstOrDefault();
    }

    /// <summary>
    /// Gets the program a channel is airing at one moment, looking past anything that is
    /// not a program. What the player needs is something to play; an interstitial or a dark
    /// stretch is not that, and the answer then is what comes next and when.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="utc">The moment; defaults to now.</param>
    /// <returns>The program on air, or null when none is.</returns>
    public Airing? ProgramOn(TvChannel channel, DateTime? utc = null)
    {
        var at = utc ?? DateTime.UtcNow;
        var airing = NowOn(channel, at);
        return airing?.Kind == AiringKind.Program ? airing : null;
    }

    /// <summary>
    /// Walks a channel's schedule over a window of time, with the gaps between programs
    /// filled in.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="fromUtc">The start of the window; the first airing may start before it.</param>
    /// <param name="toUtc">The end of the window.</param>
    /// <returns>The airings, in order.</returns>
    public IEnumerable<Airing> Window(TvChannel channel, DateTime fromUtc, DateTime toUtc)
    {
        var airings = _builder.GetSchedule(channel).Enumerate(fromUtc, toUtc);
        var withTrailers = channel.TrailersInGaps ? WithTrailers(channel, airings) : airings;
        return ScheduleEditing.Apply(
            withTrailers,
            channel.ScheduleEdits,
            fromUtc,
            toUtc,
            _builder.RuntimeOf,
            _builder.NameOf,
            channel.Name);
    }

    /// <summary>
    /// Fills the time a slot leaves over.
    /// <para>
    /// What goes in is decided in one order. A configured <see cref="TrailerSlot"/> whose time
    /// falls inside the gap wins, because it is a standing instruction and the whole reason to
    /// have written one. Otherwise the gap is filled with trailers for a programme <em>later</em>
    /// in the schedule - see <see cref="TrailedProgram"/> - and whatever time is still left over
    /// stays an empty interstitial for a client to fill with a linked trailer.
    /// </para>
    /// <para>
    /// A trailer only goes in if it fits whole: half a trailer cut off by the start of the next
    /// programme is worse than a moment of quiet.
    /// </para>
    /// </summary>
    private IEnumerable<Airing> WithTrailers(TvChannel channel, IEnumerable<Airing> airings)
    {
        // Materialised because filling a gap needs to see past it. The window is bounded by
        // the caller, so this is a few dozen entries, not a stream.
        var window = airings.ToList();

        for (var i = 0; i < window.Count; i++)
        {
            var airing = window[i];
            if (airing.Kind != AiringKind.Interstitial)
            {
                yield return airing;
                continue;
            }

            var trailed = TrailedProgram(channel, window, i) ?? airing.NextProgram;
            var slot = SlotFor(channel, airing);
            var cursor = airing.StartUtc;

            if (slot is not null)
            {
                var length = slot.ItemId != Guid.Empty
                    ? _builder.RuntimeOf(slot.ItemId)
                    : TimeSpan.FromSeconds(Math.Max(1, slot.DurationSeconds)).Ticks;

                var slotEnd = cursor + TimeSpan.FromTicks(length);
                if (length > 0 && slotEnd <= airing.EndUtc)
                {
                    var entry = slot.ItemId != Guid.Empty
                        ? new ScheduledEntry(slot.ItemId, slot.Name, null, null, length)
                        : null;

                    yield return airing with
                    {
                        Entry = entry,
                        StartUtc = cursor,
                        EndUtc = slotEnd,
                        NextProgram = trailed,
                        TrailerUrl = entry is null ? slot.Url : null
                    };
                    cursor = slotEnd;
                }
            }

            if (trailed is not null)
            {
                foreach (var trailer in _builder.TrailersFor(trailed.ItemId))
                {
                    var end = cursor + TimeSpan.FromTicks(trailer.RuntimeTicks);
                    if (end > airing.EndUtc)
                    {
                        break;
                    }

                    yield return airing with { Entry = trailer, StartUtc = cursor, EndUtc = end, NextProgram = trailed };
                    cursor = end;
                }
            }

            if (cursor < airing.EndUtc)
            {
                yield return airing with { StartUtc = cursor, NextProgram = trailed };
            }
        }
    }

    /// <summary>
    /// The programme this gap is advertising: not the one that starts the moment the gap ends,
    /// but one a few slots further on.
    /// <para>
    /// Television does not trail what is about to start - it says "at eight, the film", an hour
    /// beforehand, because the point is to make somebody stay. A trailer butted against its own
    /// programme is the one arrangement that tells the viewer nothing they are not about to find
    /// out anyway. How far ahead is <see cref="TvChannel.TrailerLookahead"/>.
    /// </para>
    /// <para>
    /// Near the end of the window there may be nothing that far ahead to point at; the search
    /// then settles for the furthest programme it can see, and finally for the next one.
    /// </para>
    /// </summary>
    private static ScheduledEntry? TrailedProgram(TvChannel channel, List<Airing> window, int index)
    {
        var wanted = Math.Max(1, channel.TrailerLookahead);
        ScheduledEntry? furthest = null;
        var seen = 0;

        for (var i = index + 1; i < window.Count; i++)
        {
            if (window[i].Kind != AiringKind.Program || window[i].Entry is null)
            {
                continue;
            }

            // Only the start of a programme is worth trailing. A programme resumed after a
            // block boundary is already half over, and announcing it would be a lie.
            if (window[i].OffsetTicks > 0)
            {
                continue;
            }

            furthest = window[i].Entry;
            if (++seen >= wanted)
            {
                return furthest;
            }
        }

        return furthest;
    }

    /// <summary>
    /// The configured slot claiming this gap, if one does: a slot claims the interstitial whose
    /// span its start time falls inside, on a day it applies to.
    /// </summary>
    private static TrailerSlot? SlotFor(TvChannel channel, Airing airing)
    {
        if (channel.TrailerSlots.Count == 0)
        {
            return null;
        }

        // Compared in local time, because a slot is written as a time on a clock - "20:10 on
        // weekdays" - and the viewer means their own clock, not UTC.
        var startLocal = airing.StartUtc.ToLocalTime();
        var endLocal = airing.EndUtc.ToLocalTime();

        foreach (var slot in channel.TrailerSlots.Where(s => s.Enabled))
        {
            if (slot.Days.Count > 0 && !slot.Days.Contains(startLocal.DayOfWeek))
            {
                continue;
            }

            var at = startLocal.Date.AddMinutes(slot.StartMinutes);
            if (at >= startLocal && at < endLocal)
            {
                return slot;
            }
        }

        return null;
    }
}
