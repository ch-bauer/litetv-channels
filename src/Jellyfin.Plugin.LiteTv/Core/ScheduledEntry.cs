namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// One playable slot in a channel's expanded queue.
/// </summary>
/// <param name="ItemId">The library item id.</param>
/// <param name="Name">The display name (episode or movie title).</param>
/// <param name="SeriesName">The series name when the item is an episode, otherwise null.</param>
/// <param name="SeriesId">The series id when the item is an episode, otherwise null.</param>
/// <param name="RuntimeTicks">The item runtime in ticks; always &gt; 0 for scheduled entries.</param>
public sealed record ScheduledEntry(Guid ItemId, string Name, string? SeriesName, Guid? SeriesId, long RuntimeTicks)
{
    /// <summary>
    /// This entry, or null when it fails the test. Reads better than repeating the entry three
    /// times at the call site, and the call sites here are all "use it only if...".
    /// </summary>
    /// <param name="wanted">The test.</param>
    /// <returns>The entry, or null.</returns>
    public ScheduledEntry? Takeaway(Func<ScheduledEntry, bool> wanted) => wanted(this) ? this : null;
}
