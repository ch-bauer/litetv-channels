namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Lays out a week for a channel nobody has curated yet.
/// <para>
/// This is the whole of what generation is for now. It runs when a channel is first given a
/// week and when the owner asks outright for the week to be laid out again; it never runs as a
/// side effect of adding a source, changing a setting or saving the configuration page,
/// because a curated week is the channel's schedule and nothing may quietly rewrite it.
/// </para>
/// <para>
/// What it does is take one week of the old computed schedule - the queue, the blocks, the
/// breaks with their adverts and trailers - and write it down as concrete airings. The
/// arithmetic that used to run on every request runs once, here, and everything downstream
/// reads a list. That is what freezes a collection: it is expanded when the week is laid out
/// and never again, so adding a title to it cannot move what is on at nine on Thursday.
/// </para>
/// </summary>
public static class WeekGenerator
{
    /// <summary>
    /// Turns one week of computed airings into a stored week.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="airings">One week of computed airings, in order, starting at
    /// <paramref name="weekStartLocal"/>.</param>
    /// <param name="weekStartLocal">The Monday 00:00 the airings were computed from.</param>
    /// <param name="timeZone">The clock the week is written on.</param>
    /// <param name="advertUrls">The channel's advert pool, by address. An advert and a linked
    /// trailer are the same thing to the schedule - an address in a break - and the only way to
    /// tell them apart afterwards is that one of them came from this list. Which they are
    /// matters on the timeline, where they are drawn and filtered separately.</param>
    /// <param name="weeks">How many weeks the schedule runs for before it repeats. One for
    /// every channel that has never been told otherwise.</param>
    /// <returns>The week, ready to store.</returns>
    public static StoredWeek Build(
        Guid channelId,
        IEnumerable<Airing> airings,
        DateTime weekStartLocal,
        TimeZoneInfo timeZone,
        IReadOnlySet<string>? advertUrls = null,
        int weeks = 1)
    {
        var cycleWeeks = Math.Max(1, weeks);
        var cycleSeconds = cycleWeeks * StoredWeek.SecondsPerWeek;
        var rows = new List<StoredAiring>();

        foreach (var airing in airings)
        {
            if (airing.Kind == AiringKind.OffAir)
            {
                // Dark air is the absence of a row, not a row. A week stores what airs.
                continue;
            }

            var startLocal = ToLocal(airing.StartUtc, timeZone);
            var endLocal = ToLocal(airing.EndUtc, timeZone);

            var start = (int)Math.Round((startLocal - weekStartLocal).TotalSeconds);
            var duration = (int)Math.Round((endLocal - startLocal).TotalSeconds);
            if (duration < WeekEditing.MinimumRemainderSeconds)
            {
                continue;
            }

            // The schedule is a loop and this is the only cut it takes: whatever is still on
            // air when the last Sunday ends is trimmed there, and the cycle starts again with
            // what the schedule says starts on Monday. A film cut by that boundary is the one
            // place a generated week reads oddly, and it is exactly what the timeline is for.
            if (start < 0)
            {
                duration += start;
                start = 0;
            }

            if (start >= cycleSeconds)
            {
                continue;
            }

            duration = Math.Min(duration, cycleSeconds - start);
            if (duration < WeekEditing.MinimumRemainderSeconds)
            {
                continue;
            }

            var isProgramme = airing.Kind == AiringKind.Program && airing.Entry is not null;
            var isAdvert = !isProgramme
                && !string.IsNullOrWhiteSpace(airing.TrailerUrl)
                && advertUrls is not null
                && advertUrls.Contains(airing.TrailerUrl);

            if (!isProgramme && airing.Entry is null && string.IsNullOrWhiteSpace(airing.TrailerUrl))
            {
                // An interstitial with nothing in it is a gap, and a gap is not stored.
                continue;
            }

            rows.Add(new StoredAiring
            {
                StartSecond = start,
                DurationSeconds = duration,
                Kind = isProgramme
                    ? StoredAiringKind.Programme
                    : isAdvert ? StoredAiringKind.Advert : StoredAiringKind.Trailer,
                ItemId = airing.Entry?.ItemId ?? Guid.Empty,
                Name = airing.Entry?.Name ?? string.Empty,
                // PlayUrl, not TrailerUrl: a programme can now BE an address - a video from a
                // YouTube playlist - and storing only the trailer's would write that programme
                // into the week with nothing to play.
                Url = airing.PlayUrl ?? string.Empty,
                OffsetTicks = isProgramme ? airing.OffsetTicks : 0,
                SeriesName = airing.Entry?.SeriesName,
                SeriesId = airing.Entry?.SeriesId,
                BlockName = airing.BlockName,
                TrailedItemId = isProgramme ? Guid.Empty : airing.NextProgram?.ItemId ?? Guid.Empty,
                TrailedName = isProgramme ? null : airing.NextProgram?.Name
            });
        }

        var now = DateTime.UtcNow;
        return new StoredWeek
        {
            ChannelId = channelId,
            Weeks = cycleWeeks,
            GeneratedUtc = now,
            ModifiedUtc = now,
            Airings = WeekEditing.Normalise(rows, cycleSeconds)
        };
    }

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo timeZone)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone),
            DateTimeKind.Unspecified);
}
