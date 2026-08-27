namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// What one row of a stored week is.
/// </summary>
public enum StoredAiringKind
{
    /// <summary>A library item airing as programming.</summary>
    Programme = 0,

    /// <summary>A trailer, held as a file or linked by address.</summary>
    Trailer = 1,

    /// <summary>An advert, always an address.</summary>
    Advert = 2,

    /// <summary>
    /// Time no row claims. Never stored - a week's file holds only what airs, and the
    /// uncovered stretches are worked out when it is read. It is a kind so that the
    /// configuration page and the guide can talk about a hole in the week without inventing
    /// a second shape for it.
    /// </summary>
    Gap = 3
}

/// <summary>
/// One thing a channel airs, at one place in its week.
/// <para>
/// Times are seconds from Monday 00:00 <em>local</em>, because that is what the owner
/// dragged it to: "Thursday at nine" means their own clock, and it stays at nine when the
/// clocks change. A row may run past the end of the week, in which case it continues into
/// the start of the same week - the week is a loop, not a page.
/// </para>
/// </summary>
public class StoredAiring
{
    /// <summary>Gets or sets the row's own id, so the page can address one of hundreds.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets when it starts, in seconds after Monday 00:00 local.</summary>
    public int StartSecond { get; set; }

    /// <summary>Gets or sets how long it runs, in seconds.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Gets or sets what kind of thing it is.</summary>
    public StoredAiringKind Kind { get; set; }

    /// <summary>Gets or sets the library item that airs, or empty for an address.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets what the guide calls it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address to play, for something the library only links to - a YouTube
    /// trailer, an advert. The client resolves it; the server never fetches it.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how far into the item this row starts. Non-zero where a programme was
    /// cut in two - by the end of the week, or by something dropped on top of it - and the
    /// second half has to resume where the first left off rather than replay it.
    /// </summary>
    public long OffsetTicks { get; set; }

    /// <summary>Gets or sets the series name, when the item is an episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the series id, when the item is an episode.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the programme block this came from when the week was laid out.</summary>
    public string? BlockName { get; set; }

    /// <summary>
    /// Gets or sets the programme a trailer or an advert leads into - what the break is
    /// announcing. Stored rather than searched for, because in a curated week the programme a
    /// break was built to trail may no longer be the next one along.
    /// </summary>
    public Guid TrailedItemId { get; set; }

    /// <summary>Gets or sets that programme's name, so the guide can print it without a lookup.</summary>
    public string? TrailedName { get; set; }

    /// <summary>Gets the second the row ends at, which may be past the end of the week.</summary>
    public int EndSecond => StartSecond + DurationSeconds;
}

/// <summary>
/// A channel's week, written down.
/// <para>
/// This is the channel's schedule - not a set of exceptions to a generated one, and not a
/// preview of one. What is here is what airs, this week and every week: the guide is this
/// list read modulo one week, forwards or backwards, forever.
/// </para>
/// <para>
/// Generation still has a job, and only one: laying out a week for a channel nobody has
/// curated yet, from its sources and settings. Once the week is stored nothing regenerates
/// over it - not adding a source, not editing a setting, not saving the configuration page.
/// The only two ways it changes are the owner moving something on the timeline and the owner
/// asking outright for the week to be laid out again, which discards the curation and says so
/// first.
/// </para>
/// <para>
/// Storing it is what makes a collection safe to schedule, quite apart from editing. A channel
/// whose source is a collection used to change underneath its own schedule every time a title
/// was added to that collection; a stored week expands the collection once, when the week is
/// laid out, so Thursday at nine stays what it was.
/// </para>
/// </summary>
public class StoredWeek
{
    /// <summary>Seconds in a week: the whole of a channel's schedule.</summary>
    public const int SecondsPerWeek = 7 * 24 * 60 * 60;

    /// <summary>Seconds in a day.</summary>
    public const int SecondsPerDay = 24 * 60 * 60;

    /// <summary>Gets or sets the file format version, for whenever this shape has to change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many weeks the schedule runs for before it repeats.
    /// <para>
    /// One, for every channel ever made until now, and the file format is unchanged for them:
    /// a missing value reads as one. More than one makes the cycle longer than a week, which
    /// is what a fortnightly film or a four-week rotation needs and what no arrangement of
    /// seven days can express. <see cref="StoredAiring.StartSecond"/> then runs over the whole
    /// cycle rather than over one week - "Thursday" means the Thursday of a particular week of
    /// it.
    /// </para>
    /// <para>
    /// Which week of the cycle is on now is not a matter of when the channel was made: it is
    /// counted from a fixed Monday, so every reader agrees and a server restart does not shift
    /// a fortnightly channel onto the other week.
    /// </para>
    /// </summary>
    public int Weeks { get; set; } = 1;

    /// <summary>
    /// Gets how long the whole schedule is, in seconds. Everything here is arithmetic on a
    /// circle of this size.
    /// </summary>
    public int CycleSeconds => Math.Max(1, Weeks) * SecondsPerWeek;

    /// <summary>Gets or sets the channel the week belongs to.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets when the week was last laid out by the generator.</summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>Gets or sets when the week was last changed at all, generation included.</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>Gets or sets what airs, in order of when.</summary>
    public List<StoredAiring> Airings { get; set; } = new();
}
