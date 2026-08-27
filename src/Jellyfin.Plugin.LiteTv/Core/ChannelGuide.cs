using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Trailers;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// The one place that answers "what is this channel airing, and when". Everything that
/// needs to know - the web guide, the published channel items, the Live TV service, the
/// session monitor - asks here, so they cannot disagree with each other about a schedule
/// they would otherwise each have resolved themselves.
/// </summary>
public sealed class ChannelGuide
{
    /// <summary>
    /// What to keep back for a trailer the schedule cannot measure. Nearly every trailer a
    /// library holds is a link the client resolves, so the server never learns its length; two
    /// and a half minutes covers the long ones, and a break that over-reserves simply runs a
    /// little quiet at the end, which is what a break used to be anyway.
    /// </summary>
    private static readonly TimeSpan LinkedTrailerReserve = TimeSpan.FromSeconds(150);

    private readonly ChannelPlaylistBuilder _builder;
    private readonly WeekStore _weeks;
    private readonly YouTubeStreamResolver _trailers;
    private readonly SponsorBlockClient _sponsorBlock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGuide"/> class.
    /// </summary>
    /// <param name="builder">The playlist builder.</param>
    /// <param name="weeks">The stored weeks.</param>
    /// <param name="trailers">Knows how long a linked trailer is, once it has resolved one.</param>
    /// <param name="sponsorBlock">Knows which parts of it will be skipped.</param>
    public ChannelGuide(
        ChannelPlaylistBuilder builder,
        WeekStore weeks,
        YouTubeStreamResolver trailers,
        SponsorBlockClient sponsorBlock)
    {
        _builder = builder;
        _weeks = weeks;
        _trailers = trailers;
        _sponsorBlock = sponsorBlock;
    }

    /// <summary>
    /// How much room a programme's linked trailer needs, when that is actually known.
    /// <para>
    /// Both halves have to be at hand already - the length from a resolution, the segments from
    /// a lookup - because the guide is walked while a request waits on it. When either is
    /// missing the caller falls back to <see cref="LinkedTrailerReserve"/>, which is what every
    /// break was sized by before any of this existed.
    /// </para>
    /// </summary>
    /// <param name="itemId">The programme being trailed.</param>
    /// <returns>The seconds to reserve, or zero when nothing is known.</returns>
    private int KnownTrailerSeconds(Guid itemId)
    {
        var url = _builder.RemoteTrailerUrl(itemId);
        if (string.IsNullOrEmpty(url))
        {
            return 0;
        }

        var length = _trailers.KnownLength(url);
        if (length <= 0)
        {
            return 0;
        }

        var segments = _sponsorBlock.SegmentsIfCached(YouTubeStreamResolver.VideoId(url));
        return PlayableLength.Of(length, segments);
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

        // The one that covers this moment, not simply the first the window hands back. A
        // window begins at the airing containing its first instant, and that airing may be
        // rebuilt into several - adverts, a trailer, then the rest of the break - the earliest
        // of which can already have finished by the time it is asked about.
        var window = Window(channel, at, at.AddMinutes(1)).ToList();
        return window.FirstOrDefault(a => a.StartUtc <= at && a.EndUtc > at)
            ?? window.FirstOrDefault(a => a.EndUtc > at)
            ?? window.FirstOrDefault();
    }

    /// <summary>
    /// Gets how long a channel takes to play everything it has, once, before it begins again.
    /// <para>
    /// A channel is an endless loop over its queue - <c>airtime % TotalTicks</c> - so it always
    /// plays through and starts over. What nobody could see was <b>how long that takes</b>: a
    /// channel built from one long series runs for months before it repeats, and a channel
    /// built from four films repeats before the evening is out. Those are very different
    /// things to have made, and until now the page could not tell them apart.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The length of one full cycle, and how many entries are in it.</returns>
    public (TimeSpan Length, int Entries) Cycle(TvChannel channel)
    {
        var lineup = _builder.GetSchedule(channel).BaseLineup;
        return lineup is null
            ? (TimeSpan.Zero, 0)
            : (TimeSpan.FromTicks(lineup.TotalTicks), lineup.Entries.Count);
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
    /// <para>
    /// A channel with a stored week is read from it and nothing else: that week <em>is</em> the
    /// schedule, and the queue, the blocks and the break arithmetic below had their say when it
    /// was laid out. A channel without one is a channel nobody has curated yet, and answers the
    /// way every channel used to.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="fromUtc">The start of the window; the first airing may start before it.</param>
    /// <param name="toUtc">The end of the window.</param>
    /// <returns>The airings, in order.</returns>
    public IEnumerable<Airing> Window(TvChannel channel, DateTime fromUtc, DateTime toUtc)
    {
        var week = _weeks.Get(channel.Id);
        return week is not null && week.Airings.Count > 0
            ? WeekReader.Enumerate(week, fromUtc, toUtc, TimeZoneInfo.Local)
            : GeneratedWindow(channel, fromUtc, toUtc);
    }

    /// <summary>
    /// Lays a week out for a channel from its sources and settings, as the schedule it would
    /// have aired with nobody curating it.
    /// <para>
    /// Only ever called because somebody asked: giving a channel its first week, or asking for
    /// one to be laid out again in place of a curated one. Nothing here saves anything - the
    /// caller does, having said out loud what it is about to discard.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="weeks">How many weeks the schedule should run for before repeating. Kept
    /// by the caller across a re-lay-out, so asking for the week again does not quietly turn a
    /// fortnight back into a week.</param>
    /// <returns>The week, unsaved.</returns>
    public StoredWeek GenerateWeek(TvChannel channel, int weeks = 1)
    {
        var cycleWeeks = Math.Max(1, weeks);
        var timeZone = TimeZoneInfo.Local;
        var nowLocal = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone),
            DateTimeKind.Unspecified);

        // Where THIS repetition of the schedule began, so a fortnight laid out on the second
        // Monday still starts at the first. Anchored absolutely - see WeekReader.CycleStart.
        var weekStartLocal = WeekReader.CycleStart(nowLocal, cycleWeeks);
        var fromUtc = ToUtc(weekStartLocal, timeZone);
        var toUtc = ToUtc(weekStartLocal.AddDays(7 * cycleWeeks), timeZone);

        // Capped because this walks a generated schedule, and a channel configured into a
        // corner - a slot grid of one minute, say - could otherwise hand back a week of
        // hundreds of thousands of rows and a file to match. The cap scales with the cycle, or
        // a four-week schedule would simply stop somewhere in week two.
        var airings = GeneratedWindow(channel, fromUtc, toUtc).Take(8192 * cycleWeeks);

        var advertUrls = channel.Adverts
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .Select(a => a.Url)
            .ToHashSet(StringComparer.Ordinal);

        return WeekGenerator.Build(channel.Id, airings, weekStartLocal, timeZone, advertUrls, cycleWeeks);
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    /// <summary>
    /// The old computed schedule: the queue and the blocks, with breaks filled and hand-made
    /// exceptions laid over the top. What every channel aired before weeks were stored, and
    /// what a channel with no stored week still airs.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="fromUtc">The start of the window.</param>
    /// <param name="toUtc">The end of the window.</param>
    /// <returns>The airings, in order.</returns>
    private IEnumerable<Airing> GeneratedWindow(TvChannel channel, DateTime fromUtc, DateTime toUtc)
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

            // The fallback is checked too, or a gap with nothing to show would be announced
            // anyway by the entry the schedule happens to have put after it.
            var trailed = TrailedProgram(channel, window, i)
                ?? airing.NextProgram?.Takeaway(next => _builder.HasTrailer(next.ItemId));
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

            // Adverts first, then the trailer, which is television's order and not an accident
            // of it: a break that ends on what is coming up leaves the viewer with the channel
            // rather than with an advert. They are drawn deterministically from the gap's own
            // start time, so every client works out the same break without anybody being told.
            foreach (var advert in AdvertsFor(channel, airing, trailed, cursor))
            {
                var end = cursor + TimeSpan.FromSeconds(Math.Max(1, advert.DurationSeconds));
                if (end > airing.EndUtc)
                {
                    break;
                }

                yield return airing with
                {
                    Entry = null,
                    StartUtc = cursor,
                    EndUtc = end,
                    NextProgram = trailed,
                    TrailerUrl = advert.Url
                };
                cursor = end;
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
    /// Which adverts fill the front of a break, and in what order.
    /// <para>
    /// Two rules, both about the joke rather than the arithmetic. Adverts of the decade the
    /// trailed programme was made in come first, because a 1980s advert before a 1980s film is
    /// the entire point and a current one is noise. And the pool is walked from a position
    /// derived from the break's own start time, so a channel that runs all evening does not
    /// play the same advert every hour - while every client still works out the same break,
    /// because the schedule has to be the same everywhere without anybody being told.
    /// </para>
    /// <para>
    /// Room is left for the trailer. An advert that fits only by pushing the trailer out has
    /// taken the break's reason for existing with it.
    /// </para>
    /// </summary>
    private IEnumerable<Advert> AdvertsFor(
        TvChannel channel,
        Airing gap,
        ScheduledEntry? trailed,
        DateTime cursor)
    {
        var pool = channel.Adverts
            .Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Url) && a.DurationSeconds > 0)
            .ToList();

        if (pool.Count == 0)
        {
            yield break;
        }

        var decade = trailed is not null ? _builder.DecadeOf(trailed.ItemId) : 0;
        var ordered = pool
            .OrderBy(a => decade > 0 && a.Decade == decade ? 0 : 1)
            .ThenBy(a => a.Decade == 0 ? 1 : 0)
            .ToList();

        // A number that is the same everywhere and different every break.
        //
        // Not the minute count itself, taken modulo the pool: breaks fall on round clock
        // intervals, and a five-advert pool with a break every hour lands on 60 % 5 = 0 every
        // single time - the same advert all evening, which is the exact thing this is for.
        // Mixing first breaks that alignment; the channel is folded in so two channels
        // breaking at once do not play the same one.
        var seed = Draw(gap.StartUtc, channel.Id, ordered.Count);

        // How long the trailer that follows will want, so the break does not end on an advert.
        //
        // Local trailer files are the easy case: their runtime is known. Nearly every trailer a
        // library has is a *link*, though, which the client resolves and which the schedule
        // therefore knows nothing about - so with an advert pool of any size the adverts would
        // eat the break and the trailer they were supposed to lead into would have nowhere to
        // go. A linked trailer gets a fixed reservation instead: not exact, and far better than
        // the zero that measuring gives.
        var localTicks = trailed is not null
            ? _builder.TrailersFor(trailed.ItemId).Select(t => t.RuntimeTicks).DefaultIfEmpty(0).Max()
            : 0;

        // A file's runtime, then the linked trailer's real playable length, then the flat
        // reservation. Only the last is a guess, and it is now the last resort rather than the
        // usual answer.
        var linkedSeconds = trailed is null ? 0 : KnownTrailerSeconds(trailed.ItemId);
        var reserved = trailed is null
            ? TimeSpan.Zero
            : localTicks > 0 ? TimeSpan.FromTicks(localTicks)
            : linkedSeconds > 0 ? TimeSpan.FromSeconds(linkedSeconds)
            : LinkedTrailerReserve;

        var room = gap.EndUtc - cursor - reserved;

        for (var i = 0; i < ordered.Count; i++)
        {
            var advert = ordered[(seed + i) % ordered.Count];
            var length = TimeSpan.FromSeconds(Math.Max(1, advert.DurationSeconds));
            if (length > room)
            {
                yield break;
            }

            room -= length;
            yield return advert;
        }
    }

    /// <summary>
    /// Picks a starting position in the advert pool for one break: the same everywhere, and
    /// different from the break before it.
    /// <para>
    /// Every client works the schedule out for itself, so this has to be a pure function of
    /// things they all know - the moment and the channel - and never a random number. The
    /// multiply-and-fold is there because the inputs are anything but random: breaks land on
    /// round intervals, and a plain modulo of the clock repeats itself exactly as often as the
    /// clock does.
    /// </para>
    /// </summary>
    /// <param name="at">When the break starts.</param>
    /// <param name="channelId">The channel, so two channels do not draw in step.</param>
    /// <param name="count">How many adverts are in the pool.</param>
    /// <returns>A position in the pool.</returns>
    public static int Draw(DateTime at, Guid channelId, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var minutes = at.Ticks / TimeSpan.TicksPerMinute;
        var salt = BitConverter.ToInt32(channelId.ToByteArray(), 0);

        var mixed = unchecked(minutes * 2654435761L + salt);
        mixed ^= mixed >> 13;
        mixed = unchecked(mixed * 1274126177L);
        mixed ^= mixed >> 16;

        return (int)(((mixed % count) + count) % count);
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
    private ScheduledEntry? TrailedProgram(TvChannel channel, List<Airing> window, int index)
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

            // And only a programme something can actually be played for. A break announcing
            // "Vorschau: Avatar" with no trailer to show spends itself in silence, and the
            // guide prints the announcement either way - so the viewer is told about a preview
            // that never existed. A programme with nothing to show is skipped over here and
            // the search goes on to the next one; if none of them has anything, the gap stays
            // a plain break.
            if (!_builder.HasTrailer(window[i].Entry!.ItemId))
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
