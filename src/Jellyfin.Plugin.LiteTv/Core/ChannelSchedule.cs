namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// What kind of thing is on air.
/// </summary>
public enum AiringKind
{
    /// <summary>A program from the channel's lineup.</summary>
    Program,

    /// <summary>
    /// The time a slot leaves over between the end of one program and the start of the
    /// next: what a real channel fills with trailers and idents.
    /// </summary>
    Interstitial,

    /// <summary>Nothing at all - the channel has no lineup for this part of the week.</summary>
    OffAir,

    /// <summary>A trailer inserted into the channel's queue as a scheduled item.</summary>
    Trailer
}

/// <summary>
/// One thing on air over one stretch of time.
/// </summary>
/// <param name="Kind">What kind of airing this is.</param>
/// <param name="Entry">What is playing, or null when nothing is.</param>
/// <param name="StartUtc">When it starts (UTC), which may be before the window asked for.</param>
/// <param name="EndUtc">When it ends (UTC).</param>
/// <param name="OffsetTicks">How far into <paramref name="Entry"/> the airing begins - non-zero
/// only where a program was cut off by the end of a block and resumes when the block next
/// comes round.</param>
/// <param name="BlockName">The program block this belongs to, or null for the channel's own lineup.</param>
/// <param name="NextProgram">The program this leads into; what the interstitial is trailing.</param>
public sealed record Airing(
    AiringKind Kind,
    ScheduledEntry? Entry,
    DateTime StartUtc,
    DateTime EndUtc,
    long OffsetTicks,
    string? BlockName,
    ScheduledEntry? NextProgram)
{
    /// <summary>
    /// Gets or sets an address to play for this airing, where the schedule names one outright
    /// rather than leaving it to be found. Only a configured trailer slot sets this; everything
    /// else in a channel is a library item, and this stays null.
    /// </summary>
    public string? TrailerUrl { get; init; }

    /// <summary>
    /// Gets the address this airing should play, whichever way it came to have one: a trailer
    /// slot that names an address outright, or an entry that IS an address because it came from
    /// a YouTube playlist. Null when the airing is a library item, which is still most of them.
    /// <para>
    /// Everything that plays an airing should read this rather than <see cref="TrailerUrl"/>,
    /// which only ever knew about the first of the two.
    /// </para>
    /// </summary>
    public string? PlayUrl => TrailerUrl ?? Entry?.Url;

    /// <summary>
    /// Gets how far into the program a viewer joining at the given moment comes in - the
    /// position to start playback at, which is the whole point of a channel.
    /// </summary>
    /// <param name="utc">When the viewer joins.</param>
    /// <returns>The position in ticks, never past the end of the program.</returns>
    public long OffsetAt(DateTime utc)
    {
        if (Entry is null)
        {
            return 0;
        }

        var offset = OffsetTicks + (utc - StartUtc).Ticks;
        return Math.Clamp(offset, 0, Entry.RuntimeTicks);
    }
}

/// <summary>
/// An ordered queue of programs, and the grid they start on.
/// </summary>
public sealed class Lineup
{
    private readonly long[] _slotted;

    /// <summary>
    /// Initializes a new instance of the <see cref="Lineup"/> class.
    /// </summary>
    /// <param name="entries">The queue, in the order it plays.</param>
    /// <param name="slotTicks">The grid programs start on; zero runs them back to back.</param>
    public Lineup(IReadOnlyList<ScheduledEntry> entries, long slotTicks)
    {
        Entries = entries.Where(e => e.RuntimeTicks > 0).ToList();
        SlotTicks = Math.Max(0, slotTicks);
        _slotted = new long[Entries.Count];
        for (var i = 0; i < Entries.Count; i++)
        {
            // A program longer than one slot takes as many whole slots as it needs, so the
            // grid still holds for everything after it.
            _slotted[i] = SlotTicks <= 0
                ? Entries[i].RuntimeTicks
                : ((Entries[i].RuntimeTicks + SlotTicks - 1) / SlotTicks) * SlotTicks;
            TotalTicks += _slotted[i];
        }
    }

    /// <summary>Gets the playable queue.</summary>
    public IReadOnlyList<ScheduledEntry> Entries { get; }

    /// <summary>Gets the grid programs start on, in ticks; zero for back to back.</summary>
    public long SlotTicks { get; }

    /// <summary>Gets how long one pass through the queue takes, slots included.</summary>
    public long TotalTicks { get; }

    /// <summary>Gets a value indicating whether there is anything to play.</summary>
    public bool IsEmpty => Entries.Count == 0 || TotalTicks <= 0;

    /// <summary>
    /// Finds where the queue stands after a given amount of airtime.
    /// </summary>
    /// <param name="airtimeTicks">Airtime since the anchor; may be negative for an anchor in the future.</param>
    /// <returns>The entry, how far into its slot the queue is, and how long that slot is.</returns>
    public (int Index, ScheduledEntry Entry, long ElapsedInSlot, long SlotLength) At(long airtimeTicks)
    {
        var position = airtimeTicks % TotalTicks;
        if (position < 0)
        {
            position += TotalTicks;
        }

        var index = 0;
        while (position >= _slotted[index])
        {
            position -= _slotted[index];
            index++;
        }

        return (index, Entries[index], position, _slotted[index]);
    }

    /// <summary>
    /// Gets the entry after the given one, wrapping round the loop.
    /// </summary>
    /// <param name="index">The current index.</param>
    /// <returns>The next entry.</returns>
    public ScheduledEntry After(int index) => Entries[(index + 1) % Entries.Count];

    /// <summary>Starts the next queue item for a weekly block occurrence.</summary>
    public (int Index, ScheduledEntry Entry, long ElapsedInSlot, long SlotLength) AtWeeklyOccurrence(long occurrence)
    {
        var index = (int)(occurrence % Entries.Count);
        if (index < 0) { index += Entries.Count; }
        return (index, Entries[index], 0, _slotted[index]);
    }
}

/// <summary>
/// A channel's schedule: the week it repeats, the lineups it airs, and the anchor the
/// whole thing is measured from. Everything here is arithmetic - given the same
/// configuration and the same clock it always answers the same, which is what lets the
/// guide promise something the player will actually air.
/// </summary>
public sealed class ChannelSchedule
{
    private readonly WeekTimeline _timeline;
    private readonly IReadOnlyDictionary<int, Lineup> _lineups;
    private readonly IReadOnlyDictionary<int, string> _blockNames;
    private readonly IReadOnlySet<int> _weeklySequenceBlocks;
    private readonly IReadOnlySet<int> _autoSizedBlocks;
    private readonly IReadOnlySet<int> _shiftToAvoidLeadingGapBlocks;
    private readonly IReadOnlyDictionary<int, BlockWindow> _blockWindows;
    private readonly TimeZoneInfo _timeZone;
    private readonly DateTime _anchorUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSchedule"/> class.
    /// </summary>
    /// <param name="timeline">The week the channel repeats.</param>
    /// <param name="lineups">The queue each lineup airs, by owner.</param>
    /// <param name="blockNames">The display name of each block, by owner.</param>
    /// <param name="anchorUtc">The schedule zero point.</param>
    /// <param name="timeZone">The time zone the blocks are stated in.</param>
    public ChannelSchedule(
        WeekTimeline timeline,
        IReadOnlyDictionary<int, Lineup> lineups,
        IReadOnlyDictionary<int, string> blockNames,
        IReadOnlySet<int> weeklySequenceBlocks,
        DateTime anchorUtc,
        TimeZoneInfo timeZone,
        IReadOnlySet<int>? autoSizedBlocks = null,
        IReadOnlyDictionary<int, BlockWindow>? blockWindows = null,
        IReadOnlySet<int>? shiftToAvoidLeadingGapBlocks = null)
    {
        _timeline = timeline;
        _lineups = lineups;
        _blockNames = blockNames;
        _weeklySequenceBlocks = weeklySequenceBlocks;
        _autoSizedBlocks = autoSizedBlocks ?? new HashSet<int>();
        _shiftToAvoidLeadingGapBlocks = shiftToAvoidLeadingGapBlocks ?? new HashSet<int>();
        _blockWindows = blockWindows ?? new Dictionary<int, BlockWindow>();
        _anchorUtc = anchorUtc;
        _timeZone = timeZone;
    }

    /// <summary>
    /// Gets a value indicating whether the channel has anything to air at all.
    /// </summary>
    public bool IsSilent => _lineups.Values.All(l => l.IsEmpty);

    /// <summary>
    /// Gets the queue the channel airs when no block covers the moment - its own lineup, as
    /// opposed to a block's. This is the one that loops, and its length is how long the channel
    /// takes to play everything it has before starting again.
    /// </summary>
    public Lineup? BaseLineup =>
        _lineups.TryGetValue(WeekTimeline.BaseLineup, out var lineup) ? lineup : null;

    /// <summary>
    /// Walks the schedule over a window of time.
    /// </summary>
    /// <param name="fromUtc">Where to start; the first airing returned is the one covering it,
    /// so its start may be earlier.</param>
    /// <param name="toUtc">Where to stop.</param>
    /// <returns>The airings, in order, without gaps between them.</returns>
    public IEnumerable<Airing> Enumerate(DateTime fromUtc, DateTime toUtc)
    {
        var cursor = ToLocal(fromUtc);
        var end = ToLocal(toUtc);
        var anchorLocal = ToLocal(_anchorUtc);
        var baseAirtimeTail = 0L;
        DateTime? baseResumeAt = null;
        DateTime? baseResumeEnd = null;
        long? baseResumeAirtime = null;
        var releasedAutoSpans = new HashSet<long>();
        var shiftedFilmBlocks = new List<ShiftedFilmBlock>();

        // A window is walked, not solved: a block boundary can cut a program short and the
        // next lineup starts wherever it left off, so each step depends on the one before.
        // The cap keeps a configuration that somehow fails to advance from spinning here.
        for (var step = 0; cursor < end && step < 4096; step++)
        {
            if (baseResumeAt is { } resumeAt && baseResumeEnd is { } resumeEnd && cursor >= resumeEnd)
            {
                // The virtual base run has reached the original block boundary. From here on
                // the normal timeline airtime is short by the released tail, so carry that tail
                // forward and return to the ordinary calculation.
                if (baseResumeAirtime is { } resumeAirtime)
                {
                    baseAirtimeTail = resumeAirtime + (resumeEnd - resumeAt).Ticks
                        - (Airtime(WeekTimeline.BaseLineup, resumeEnd) - Airtime(WeekTimeline.BaseLineup, anchorLocal));
                }
                baseResumeAt = null;
                baseResumeEnd = null;
                baseResumeAirtime = null;
            }

            var absoluteMinute = WeekTimeline.AbsoluteMinute(cursor);
            var owner = _timeline.OwnerAt(absoluteMinute);
            var spanStartMinute = _timeline.SpanStartAt(absoluteMinute);
            var spanStart = MinuteToLocal(spanStartMinute, DateTime.MinValue);
            var spanEnd = MinuteToLocal(_timeline.NextChangeAfter(absoluteMinute), DateTime.MaxValue);
            var shiftedFilm = shiftedFilmBlocks.FirstOrDefault(block => cursor >= block.NominalStart && cursor < block.End);
            if (shiftedFilm is not null)
            {
                owner = shiftedFilm.Owner;
                spanStart = shiftedFilm.Start;
                spanEnd = shiftedFilm.End;
            }

            // An auto-sized weekly film block releases the channel back to its base lineup as
            // soon as this week's selected film ends, rather than keeping the rest of its
            // fallback window as a large empty block.
            if (_weeklySequenceBlocks.Contains(owner) && _autoSizedBlocks.Contains(owner))
            {
                var release = spanStart + TimeSpan.FromTicks(lineupLength(owner, cursor, anchorLocal));
                if (cursor >= release)
                {
                    // Once an auto-sized film has ended, the rest of the configured block window
                    // belongs to the base lineup. The timeline still reports the original block
                    // until its configured end, so remember the released span and count its tail
                    // exactly once. Without this, every loop iteration recalculated the base
                    // queue at the airtime immediately before the film and repeated that episode.
                    if (spanStartMinute is { } start && releasedAutoSpans.Add(start))
                    {
                        baseResumeAt = release;
                        baseResumeEnd = spanEnd;
                        if (_lineups.TryGetValue(WeekTimeline.BaseLineup, out var baseLineup) && !baseLineup.IsEmpty)
                        {
                            // The film replaces the programme that would have crossed its
                            // start. Resume at the NEXT complete base slot, never at that
                            // programme's old clock position: otherwise it reappears after the
                            // film as a few-minute tail.
                            var baseAtBlock = Airtime(WeekTimeline.BaseLineup, spanStart)
                                - Airtime(WeekTimeline.BaseLineup, anchorLocal)
                                + baseAirtimeTail;
                            var (_, _, elapsed, baseSlotLength) = baseLineup.At(baseAtBlock);
                            baseResumeAirtime = baseAtBlock - elapsed + baseSlotLength;
                        }
                    }

                    owner = WeekTimeline.BaseLineup;
                    spanStart = release;
                }
            }

            if (!_lineups.TryGetValue(owner, out var lineup) || lineup.IsEmpty)
            {
                // Off air until the next changeover, and said so - not until the end of
                // whatever window happened to be asked for. A viewer looking at a dark
                // channel wants to know when it comes back on. A channel with nothing at
                // all never changes over, and "off air for the next week" is as much as
                // there is to say about it.
                var offAirEnd = spanEnd == DateTime.MaxValue ? cursor.AddDays(7) : spanEnd;
                yield return new Airing(AiringKind.OffAir, null, ToUtc(cursor), ToUtc(offAirEnd), 0, BlockName(owner), null);
                cursor = offAirEnd;
                continue;
            }

            var airtime = Airtime(owner, cursor) - Airtime(owner, anchorLocal);
            if (owner == WeekTimeline.BaseLineup)
            {
                airtime += baseAirtimeTail;
                if (baseResumeAt is { } currentResumeAt
                    && baseResumeEnd is { } currentResumeEnd
                    && baseResumeAirtime is { } currentResumeAirtime
                    && cursor < currentResumeEnd)
                {
                    // The timeline still calls this part of the day a block, but an auto-sized
                    // film has already ended. Let the base queue advance with the wall clock
                    // until that original span ends; otherwise every airing starts at the same
                    // pre-film cursor.
                    airtime = currentResumeAirtime + (cursor - currentResumeAt).Ticks;
                }
            }

            var (index, entry, elapsedInSlot, slotLength) = _weeklySequenceBlocks.Contains(owner)
                ? lineup.AtWeeklyOccurrence(BlockOccurrence(owner, spanStart, anchorLocal))
                : lineup.At(airtime);

            // A weekly sequence block owns one item for the occurrence. Do not start that same
            // item again when it ends before the configured window; the next item belongs to the
            // next week. The remaining window is an intentional interstitial.
            if (_weeklySequenceBlocks.Contains(owner)
                && cursor >= spanStart + TimeSpan.FromTicks(slotLength))
            {
                yield return new Airing(AiringKind.Interstitial, null, ToUtc(cursor), ToUtc(spanEnd), 0, BlockName(owner), null);
                cursor = spanEnd;
                continue;
            }

            // Inside a slot there are two phases: the program, then whatever the slot has
            // left over. They are separate airings - the guide has to be able to say "the
            // film runs to 21:40 and the next one starts at 22:00".
            var inProgram = elapsedInSlot < entry.RuntimeTicks;
            var phaseElapsed = inProgram ? elapsedInSlot : elapsedInSlot - entry.RuntimeTicks;
            var phaseLength = inProgram ? entry.RuntimeTicks : slotLength - entry.RuntimeTicks;

            // Where this airing really began: back up by however far into it we are, but
            // never past the start of the block - before that it was another lineup's turn,
            // and this program was cut off waiting for the block to come round again.
            var phaseStart = Max(spanStart, cursor - TimeSpan.FromTicks(phaseElapsed));
            var offsetAtStart = phaseElapsed - (cursor - phaseStart).Ticks;
            var phaseEnd = Min(phaseStart + TimeSpan.FromTicks(phaseLength - offsetAtStart), spanEnd);

            // A scheduled block is a promise about its clock time.  Do not begin a base
            // programme that would be cut by that promise: reserve the tail as a break so the
            // guide can offer the upcoming film's trailer (and its normal Skip action) instead.
            // The base queue keeps its clock position, so the episode is never aired in two
            // pieces on opposite sides of the film block.
            if (owner == WeekTimeline.BaseLineup
                && inProgram
                && phaseEnd == spanEnd
                && spanEnd != DateTime.MaxValue
                && phaseStart + TimeSpan.FromTicks(phaseLength - offsetAtStart) > spanEnd)
            {
                var nextOwner = _timeline.OwnerAt(WeekTimeline.AbsoluteMinute(spanEnd));
                if (nextOwner != WeekTimeline.BaseLineup
                    && _lineups.TryGetValue(nextOwner, out var nextLineup)
                    && !nextLineup.IsEmpty)
                {
                    var naturalEnd = phaseStart + TimeSpan.FromTicks(phaseLength - offsetAtStart);
                    if (_shiftToAvoidLeadingGapBlocks.Contains(nextOwner)
                        && naturalEnd > spanEnd
                        && naturalEnd - spanEnd <= TimeSpan.FromMinutes(30))
                    {
                        // Keep the episode whole and let the film follow it. The effective
                        // block window moves by exactly that small overrun, including its end,
                        // so a film's measured length remains whole as well.
                        var originalBlockEnd = MinuteToLocal(
                            _timeline.NextChangeAfter(WeekTimeline.AbsoluteMinute(spanEnd)),
                            DateTime.MaxValue);
                        shiftedFilmBlocks.Add(new ShiftedFilmBlock(
                            nextOwner,
                            spanEnd,
                            naturalEnd,
                            originalBlockEnd + (naturalEnd - spanEnd)));
                        yield return new Airing(
                            entry.IsTrailer ? AiringKind.Trailer : AiringKind.Program,
                            entry,
                            ToUtc(phaseStart),
                            ToUtc(naturalEnd),
                            offsetAtStart,
                            BlockName(owner),
                            lineup.After(index));
                        cursor = naturalEnd;
                        continue;
                    }
                    var (_, nextProgram, _, _) = _weeklySequenceBlocks.Contains(nextOwner)
                        ? nextLineup.AtWeeklyOccurrence(BlockOccurrence(nextOwner, spanEnd, anchorLocal))
                        : nextLineup.At(Airtime(nextOwner, spanEnd) - Airtime(nextOwner, anchorLocal));
                    yield return new Airing(AiringKind.Interstitial, null, ToUtc(phaseStart), ToUtc(spanEnd), 0, null, nextProgram);
                    cursor = spanEnd;
                    continue;
                }
            }

            yield return new Airing(
                inProgram
                    ? (entry.IsTrailer ? AiringKind.Trailer : AiringKind.Program)
                    : AiringKind.Interstitial,
                inProgram ? entry : null,
                ToUtc(phaseStart),
                ToUtc(phaseEnd),
                inProgram ? offsetAtStart : 0,
                BlockName(owner),
                lineup.After(index));

            cursor = phaseEnd > cursor ? phaseEnd : cursor.AddMinutes(1);
        }
    }

    private long lineupLength(int owner, DateTime cursor, DateTime anchorLocal)
    {
        var lineup = _lineups[owner];
        var selected = lineup.AtWeeklyOccurrence(BlockOccurrence(owner, cursor, anchorLocal));
        return selected.SlotLength;
    }

    private long BlockOccurrence(int owner, DateTime local, DateTime anchorLocal)
    {
        if (!_blockWindows.TryGetValue(owner, out var block))
        {
            return WeeksSinceAnchor(local, anchorLocal);
        }

        var days = block.Days.Count > 0
            ? block.Days
            : Enum.GetValues<DayOfWeek>();
        var daysPerWeek = days.Count;
        var daysFromAnchor = (local.Date - anchorLocal.Date).Days;
        var week = FloorDiv(daysFromAnchor, 7);
        var weekStart = anchorLocal.Date.AddDays(week * 7);
        var day = (local.Date - weekStart).Days;
        var minutes = (int)(local - local.Date).TotalMinutes;
        var inWeek = days.Count(d => (int)d == (int)local.DayOfWeek && block.StartMinutes <= minutes);

        foreach (var candidate in days)
        {
            var candidateDay = (int)candidate;
            var localDay = (int)local.DayOfWeek;
            var mondayCandidate = ((candidateDay + 6) % 7);
            var mondayLocal = ((localDay + 6) % 7);
            if (mondayCandidate < mondayLocal)
            {
                inWeek++;
            }
        }

        return (week * daysPerWeek) + Math.Max(0, inWeek - 1);
    }

    private static long FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Gets what is on air at one moment.
    /// </summary>
    /// <param name="utc">The moment.</param>
    /// <returns>The airing covering it, or null when the channel has nothing at all.</returns>
    public Airing? At(DateTime utc)
    {
        // A movable film start is determined by the programme immediately before it. Walk from
        // the local day boundary so a one-minute lookup has the same decision as the full guide.
        var localStart = ToLocal(utc).Date;
        var fromUtc = ToUtc(localStart);
        return Enumerate(fromUtc, utc.AddMinutes(1))
            .LastOrDefault(airing => airing.StartUtc <= utc && airing.EndUtc > utc);
    }

    private sealed record ShiftedFilmBlock(int Owner, DateTime NominalStart, DateTime Start, DateTime End);

    private string? BlockName(int owner)
        => _blockNames.TryGetValue(owner, out var name) ? name : null;

    private static long WeeksSinceAnchor(DateTime local, DateTime anchorLocal)
    {
        var days = (local.Date - anchorLocal.Date).Days;
        return days >= 0 ? days / 7 : -(((-days) + 6) / 7);
    }

    /// <summary>
    /// Gets how much airtime a lineup has had by a given local time, in ticks. Only the
    /// difference between two of these means anything; the zero point is arbitrary.
    /// </summary>
    private long Airtime(int owner, DateTime local)
    {
        var absoluteMinute = WeekTimeline.AbsoluteMinute(local);
        var ticks = _timeline.AirtimeMinutesUpTo(owner, absoluteMinute) * TimeSpan.TicksPerMinute;

        // The part-minute only counts when this lineup is the one currently on air.
        if (_timeline.OwnerAt(absoluteMinute) == owner)
        {
            ticks += local.Ticks % TimeSpan.TicksPerMinute;
        }

        return ticks;
    }

    private static DateTime MinuteToLocal(long? absoluteMinute, DateTime fallback)
        => absoluteMinute is { } minute ? new DateTime(minute * TimeSpan.TicksPerMinute, DateTimeKind.Unspecified) : fallback;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _timeZone), DateTimeKind.Unspecified);

    private DateTime ToUtc(DateTime local)
    {
        if (local >= DateTime.MaxValue.AddDays(-2))
        {
            return DateTime.MaxValue;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // The hour that daylight saving skips is not a local time at all. It is only ever
        // reached by arithmetic on this side, so move through it rather than refusing.
        if (_timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _timeZone);
    }
}
