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
    /// <summary>Gets a value indicating whether this entry is a scheduled trailer.</summary>
    public bool IsTrailer { get; init; }

    /// <summary>Gets the library item this trailer advertises, when this is a trailer.</summary>
    public Guid? TrailerForItemId { get; init; }

    /// <summary>Gets the display name of the library item this trailer advertises.</summary>
    public string? TrailerForName { get; init; }

    /// <summary>
    /// Gets the address to play, for an entry the library has never heard of - a video from a
    /// YouTube playlist. Null for everything else, where <see cref="ItemId"/> names the thing.
    /// <para>
    /// An entry has one or the other, never both: <see cref="ItemId"/> is <see cref="Guid.Empty"/>
    /// exactly when this is set.
    /// </para>
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry is an address rather than a library item.
    /// </summary>
    public bool IsAddress => !string.IsNullOrEmpty(Url);

    /// <summary>
    /// This entry, or null when it fails the test. Reads better than repeating the entry three
    /// times at the call site, and the call sites here are all "use it only if...".
    /// </summary>
    /// <param name="wanted">The test.</param>
    /// <returns>The entry, or null.</returns>
    public ScheduledEntry? Takeaway(Func<ScheduledEntry, bool> wanted) => wanted(this) ? this : null;
}
