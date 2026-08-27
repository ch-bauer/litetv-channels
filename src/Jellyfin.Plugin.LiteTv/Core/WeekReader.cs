namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Reads a stored week as a schedule: what is on now, and what is on over any window of time,
/// forwards or backwards, forever.
/// <para>
/// There is no horizon and no rollover to build. The week repeats, so a moment is turned into
/// a second of the week and looked up, and a window is the week laid down as many times as the
/// window is long. March next year is answerable as cheaply as this evening.
/// </para>
/// <para>
/// All of it in local wall-clock time, so a programme at nine stays at nine across a
/// daylight-saving change. The hour the clocks move is aired twice or not at all, which is
/// exactly what happens to a broadcast schedule.
/// </para>
/// </summary>
public static class WeekReader
{
    /// <summary>
    /// How long a hole in the week has to be before it counts as the channel being off air
    /// rather than a break. An hour: a break is minutes, and a viewer looking at an evening
    /// nobody scheduled anything into should be told the channel is dark rather than that it
    /// is between programmes.
    /// </summary>
    public static readonly TimeSpan DarkAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Walks a stored week over a window of time.
    /// </summary>
    /// <param name="week">The stored week.</param>
    /// <param name="fromUtc">Where the window starts; the first airing returned is the one
    /// covering it, so its start may be earlier.</param>
    /// <param name="toUtc">Where it ends.</param>
    /// <param name="timeZone">The clock the week is written on.</param>
    /// <returns>The airings, in order, covering the window without gaps.</returns>
    public static IEnumerable<Airing> Enumerate(
        StoredWeek week,
        DateTime fromUtc,
        DateTime toUtc,
        TimeZoneInfo timeZone)
    {
        var rows = BuildRows(week);
        if (rows.Count == 0)
        {
            yield return new Airing(AiringKind.OffAir, null, fromUtc, toUtc, 0, null, null);
            yield break;
        }

        var fromLocal = ToLocal(fromUtc, timeZone);
        var toLocal = ToLocal(toUtc, timeZone);

        // One week back, because the airing covering the start of the window may have begun in
        // the copy of the week before this one - a film that started at 23:40 last night.
        var weekStart = WeekStart(fromLocal).AddDays(-7);

        while (weekStart < toLocal)
        {
            foreach (var (row, kind, next) in rows)
            {
                var startLocal = weekStart.AddSeconds(row.StartSecond);
                var endLocal = startLocal.AddSeconds(row.DurationSeconds);
                if (endLocal <= fromLocal || startLocal >= toLocal)
                {
                    continue;
                }

            // A programme is a library item, or - since playlists became content - an address
            // with no library item behind it at all. Both are real programmes; only a row with
            // neither is nothing.
            var isAddressProgramme = row.ItemId == Guid.Empty
                && kind == AiringKind.Program
                && !string.IsNullOrWhiteSpace(row.Url);

            var entry = row.ItemId != Guid.Empty || isAddressProgramme
                ? new ScheduledEntry(
                    row.ItemId,
                    row.Name,
                    row.SeriesName,
                    row.SeriesId,
                    row.OffsetTicks + (row.DurationSeconds * TimeSpan.TicksPerSecond))
                {
                    Url = isAddressProgramme ? row.Url : null
                }
                : null;

                yield return new Airing(
                    kind,
                    kind == AiringKind.Program ? entry : null,
                    ToUtc(startLocal, timeZone),
                    ToUtc(endLocal, timeZone),
                    kind == AiringKind.Program ? row.OffsetTicks : 0,
                    string.IsNullOrEmpty(row.BlockName) ? null : row.BlockName,
                    next)
                {
                    // A trailer or an advert is an address the client resolves. A programme
                    // used to name none; one from a playlist does, and it travels on the entry
                    // instead - so Airing.PlayUrl answers for both.
                    TrailerUrl = kind == AiringKind.Program || string.IsNullOrWhiteSpace(row.Url) ? null : row.Url
                };
            }

            weekStart = weekStart.AddDays(7);
        }
    }

    /// <summary>
    /// Gets what a stored week is airing at one moment.
    /// </summary>
    /// <param name="week">The stored week.</param>
    /// <param name="utc">The moment.</param>
    /// <param name="timeZone">The clock the week is written on.</param>
    /// <returns>The airing covering it.</returns>
    public static Airing? At(StoredWeek week, DateTime utc, TimeZoneInfo timeZone)
        => Enumerate(week, utc, utc.AddSeconds(1), timeZone)
            .FirstOrDefault(a => a.StartUtc <= utc && a.EndUtc > utc);

    /// <summary>
    /// The week as rows to lay down: what is stored, plus the holes between them, each already
    /// knowing which programme it leads into.
    /// <para>
    /// Worked out once for the whole window rather than per repetition, because it is the same
    /// week every time and the answer cannot differ.
    /// </para>
    /// </summary>
    /// <param name="week">The stored week.</param>
    /// <returns>The rows, in order of when they start.</returns>
    public static List<(StoredAiring Row, AiringKind Kind, ScheduledEntry? Next)> BuildRows(StoredWeek week)
    {
        var stored = week.Airings
            .Where(a => a.DurationSeconds > 0)
            .OrderBy(a => a.StartSecond)
            .ToList();

        if (stored.Count == 0)
        {
            return new List<(StoredAiring, AiringKind, ScheduledEntry?)>();
        }

        var all = new List<StoredAiring>(stored);
        foreach (var (start, duration) in WeekEditing.Gaps(stored))
        {
            all.Add(new StoredAiring
            {
                Id = Guid.Empty,
                StartSecond = start,
                DurationSeconds = duration,
                Kind = StoredAiringKind.Gap
            });
        }

        all = all.OrderBy(a => a.StartSecond).ToList();

        var result = new List<(StoredAiring, AiringKind, ScheduledEntry?)>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            var row = all[i];
            var kind = row.Kind switch
            {
                StoredAiringKind.Programme => AiringKind.Program,
                StoredAiringKind.Gap when row.DurationSeconds >= DarkAfter.TotalSeconds => AiringKind.OffAir,
                _ => AiringKind.Interstitial
            };

            result.Add((row, kind, NextProgramme(all, i)));
        }

        return result;
    }

    /// <summary>
    /// The programme a row leads into: what a break announces, and what the guide shows as
    /// "next" while something is still on.
    /// <para>
    /// A break says what it was built to say. When the week was laid out the generator chose a
    /// programme a few slots ahead - television trails "at eight, the film", not the thing
    /// starting in ninety seconds - and that choice is stored with the break, because in a
    /// curated week the programme it was trailing may no longer be the next one along. Only
    /// when a row carries no such choice does this fall back to looking forward for one.
    /// </para>
    /// </summary>
    /// <param name="rows">Every row of the week, in order.</param>
    /// <param name="index">The row to look forward from.</param>
    /// <returns>The programme, or null when the week holds none at all.</returns>
    private static ScheduledEntry? NextProgramme(List<StoredAiring> rows, int index)
    {
        var row = rows[index];
        if (row.TrailedItemId != Guid.Empty)
        {
            return new ScheduledEntry(row.TrailedItemId, row.TrailedName ?? string.Empty, null, null, 0);
        }

        // Round the loop, so the last row of the week points at Monday morning rather than at
        // nothing. Stops after one turn: a week of nothing but adverts has no programme to find.
        for (var step = 1; step <= rows.Count; step++)
        {
            var candidate = rows[(index + step) % rows.Count];
            if (candidate.Kind == StoredAiringKind.Programme && candidate.OffsetTicks == 0)
            {
                return new ScheduledEntry(
                    candidate.ItemId,
                    candidate.Name,
                    candidate.SeriesName,
                    candidate.SeriesId,
                    candidate.DurationSeconds * TimeSpan.TicksPerSecond);
            }
        }

        return null;
    }

    /// <summary>
    /// Monday 00:00 of the local week a local time falls in.
    /// </summary>
    /// <param name="local">The local time.</param>
    /// <returns>The start of its week.</returns>
    public static DateTime WeekStart(DateTime local)
    {
        var dayIndex = ((int)local.DayOfWeek + 6) % 7;
        return local.Date.AddDays(-dayIndex);
    }

    /// <summary>
    /// The second of the week a local time falls at, counting from Monday 00:00.
    /// </summary>
    /// <param name="local">The local time.</param>
    /// <returns>The second of the week.</returns>
    public static int SecondOfWeek(DateTime local)
        => (int)(local - WeekStart(local)).TotalSeconds;

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo timeZone)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone),
            DateTimeKind.Unspecified);

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // The hour daylight saving skips is not a local time at all, and a schedule written on
        // a clock reaches it by arithmetic every spring. Move through it rather than refuse.
        if (timeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }
}
