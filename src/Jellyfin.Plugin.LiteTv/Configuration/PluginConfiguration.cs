using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiteTv.Configuration;

/// <summary>
/// Plugin configuration: the channel definitions and UI options.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the defined channels.
    /// </summary>
    public List<TvChannel> Channels { get; set; } = new();

    /// <summary>
    /// Gets or sets the name of the Jellyfin account channel playback runs under.
    /// <para>
    /// This is what keeps channel viewing off the account people watch under: the server
    /// decides whose watch state a playback belongs to from the token the request carries,
    /// so a channel played with this account's token records against this account and
    /// leaves the real one untouched. The account is created on first use, hidden from the
    /// login screen, and stripped of everything but the ability to play.
    /// </para>
    /// </summary>
    public string ChannelUserName { get; set; } = PluginConfigurationDefaults.ChannelUserName;

    /// <summary>
    /// Gets or sets the password of that account. Generated on first use and never typed
    /// by anyone; it is stored so the plugin can authenticate as the account after a
    /// restart. Anyone who can read this file can already read every other plugin's
    /// configuration, so it is kept in plain sight rather than pretending otherwise.
    /// </summary>
    public string ChannelUserPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the parts of a trailer that are not the trailer
    /// are skipped, using SponsorBlock's public database.
    /// <para>
    /// On by default. German trailers mostly come from channels that wrap them in a branded
    /// card and a plea to subscribe, and a minute-long break has no room for either. The
    /// lookup asks about a four-character hash of the video id rather than the id itself, so
    /// the service is never told what is being watched; turning this off stops the request
    /// being made at all.
    /// </para>
    /// </summary>
    public bool SkipTrailerSegments { get; set; } = true;

    /// <summary>
    /// Gets or sets which YouTube client the trailer resolver pretends to be, or an empty
    /// string to try them all in order.
    /// <para>
    /// Here because this is the one thing about trailers that cannot be settled from a server.
    /// What YouTube hands over depends on who is asking, and it differs by client, by day, and
    /// by whether the asker looks like a real device: Android VR returns unobfuscated addresses
    /// for every rendition on a phone running microG, and answers LOGIN_REQUIRED from a
    /// server that has no Google identity to offer. Whoever is testing on the television can
    /// name a client here and see for themselves rather than take a measurement's word for it.
    /// </para>
    /// </summary>
    public string YouTubeClient { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cookie header from a logged-in YouTube session, or an empty string to
    /// ask anonymously.
    /// <para>
    /// What YouTube hands an anonymous asker is one 360p muxed address and nineteen renditions
    /// with no address at all - measured, not assumed. An account changes that: the same
    /// request signed as a logged-in session gets the addresses. This is where the session is
    /// kept.
    /// </para>
    /// <para>
    /// <b>Use an account you do not care about.</b> This is a server asking YouTube for streams
    /// on a schedule, which is the shape of thing accounts get rate-limited and banned for, and
    /// the account here is one paste away from being the account everything else is on.
    /// </para>
    /// <para>
    /// <b>It is stored in plain text</b>, like every other plugin setting on this server, and it
    /// is a live credential - anybody who can read the configuration can act as that account.
    /// Never put a dump of this configuration anywhere public.
    /// </para>
    /// </summary>
    public string YouTubeCookie { get; set; } = string.Empty;

}

/// <summary>
/// A trailer the schedule places itself, at a time of the week the viewer chose.
/// <para>
/// Unlike the automatic filling, which trails whatever happens to be on next, a slot is a
/// standing instruction: this thing, at this time, on these days. It claims the interstitial
/// its start time falls inside, so it only ever airs where the schedule already left room.
/// </para>
/// </summary>
public class TrailerSlot
{
    /// <summary>Gets or sets what to call it in the guide.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the slot is in use.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the time of day it airs, in minutes past midnight.</summary>
    public int StartMinutes { get; set; }

    /// <summary>Gets or sets the days it applies to. Empty means every day.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>
    /// Gets or sets the library item to play - a trailer, a clip, anything with a runtime.
    /// Takes precedence over <see cref="Url"/> when both are set, because a file the server
    /// holds can be scheduled to the second and an address cannot.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets an address to play instead, for something the library only links to.
    /// The client resolves and plays it; the server never fetches it.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long to give it, in seconds, when it is an address. A library item
    /// brings its own runtime; an address has none the server can read.
    /// </summary>
    public int DurationSeconds { get; set; } = 30;
}

/// <summary>
/// An advert the channel plays in its breaks.
/// <para>
/// A break is already filled with trailers for what is coming up; an advert is the same
/// machinery pointed at something that is not in the library at all. Which is the joke: a
/// 1980s advert before a 1980s film is what makes a made-up channel feel like a channel, and
/// a current one is just noise. Hence <see cref="Decade"/>.
/// </para>
/// <para>
/// An address rather than a file, resolved by the client the same way a linked trailer is. The
/// server never fetches it and nothing is reported to Jellyfin: an advert is not library
/// content and has no item to record against.
/// </para>
/// </summary>
public class Advert
{
    /// <summary>Gets or sets what to call it in the guide.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the address to play - a YouTube link, or anything the client can resolve.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long to give it, in seconds. An address has no runtime the server can
    /// read, and a break is a fixed number of minutes that the arithmetic has to fit.
    /// </summary>
    public int DurationSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the decade this advert belongs to - 1980 for the eighties - or zero when it
    /// belongs to none. Adverts of the decade the programme was made in are preferred, which is
    /// the whole charm of the thing.
    /// </summary>
    public int Decade { get; set; }

    /// <summary>Gets or sets a value indicating whether it is in rotation.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// What a channel does instead of what it would have done, at one fixed moment.
/// <para>
/// A LiteTV schedule is generated rather than stored: the anchor plus the queue decide what is
/// on at any instant, which is what makes it identical on every client, free to compute and
/// endless - the channel loops forever without anybody writing down next March. Editing it
/// therefore cannot mean editing a stored list, because there is none. It means keeping a small
/// set of exceptions and laying them over what the generator says.
/// </para>
/// <para>
/// Each edit is an appointment: from <see cref="StartUtc"/>, for as long as its content runs.
/// Whatever the generator had there is trimmed around it, exactly as a real broadcaster's
/// schedule bends around a fixture. That keeps the loop, keeps every client agreeing, and costs
/// only what is actually different from the automatic schedule.
/// </para>
/// <para>
/// Keyed to an absolute instant rather than to a weekly slot, deliberately. An edit made by
/// dragging a programme in a timeline is about *that* airing - "not this Saturday's film, that
/// one" - and a viewer who wants something every week has <see cref="TrailerSlot"/>, which is
/// exactly a standing weekly instruction. Absolute edits also expire by themselves: one in the
/// past can never fire again, so the list is prunable rather than permanent.
/// </para>
/// </summary>
public class ScheduleEdit
{
    /// <summary>Gets or sets the edit's own id, so the page can address one of many.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets a value indicating whether the edit is in force.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets when it starts.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Gets or sets what it does.</summary>
    public ScheduleEditKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the library item to air. It brings its own runtime, which is what decides
    /// how much of the generated schedule this edit displaces.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets an address to air instead - a trailer, an advert, anything the library only
    /// links to. The client resolves and plays it; the server never fetches it.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long to give it, in seconds. Required for an address and for
    /// <see cref="ScheduleEditKind.Remove"/>, which has no content to take a runtime from;
    /// ignored for a library item, which brings its own.
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>Gets or sets what to call it in the guide. Empty takes the item's own name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>What a <see cref="ScheduleEdit"/> does to the generated schedule.</summary>
public enum ScheduleEditKind
{
    /// <summary>Air this instead of whatever the generator put here.</summary>
    Air = 0,

    /// <summary>Air nothing here at all, leaving the channel dark for the duration.</summary>
    Remove = 1
}

/// <summary>
/// A virtual TV channel: an ordered, endlessly looping queue of library content.
/// The schedule is fully deterministic: what is on "now" derives from the wall clock,
/// the anchor timestamp and the item runtimes. (v2 may add optional time blocks.)
/// </summary>
public class TvChannel
{
    /// <summary>
    /// Gets or sets the channel id.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the channel display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the channel is on air.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the schedule zero point (UTC). The loop position is
    /// (now - anchor) modulo the total queue runtime.
    /// </summary>
    public DateTime AnchorUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the ordered content sources making up the loop.
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the changes made to this channel's generated schedule by hand: what airs
    /// instead, and what airs not at all. Empty - the usual case - means the channel is exactly
    /// what its queue and anchor say it is.
    /// </summary>
    public List<ScheduleEdit> ScheduleEdits { get; set; } = new();

    /// <summary>
    /// Gets or sets the adverts this channel plays in its breaks, drawn from in television's
    /// order: adverts first, the trailer last, so a break ends on what the channel is about to
    /// show. Empty - the usual case - means breaks are trailers only.
    /// </summary>
    public List<Advert> Adverts { get; set; } = new();

    /// <summary>
    /// Gets or sets how many consecutive items are taken from each source before
    /// rotating to the next one, interleaving the channel's sources. For example, two
    /// series with a block of 2 play as S1E1, S1E2, S2E1, S2E2, S1E3, S1E4, ... The
    /// default of 0 keeps the classic behaviour: each source is played in full before
    /// the next (a marathon).
    /// </summary>
    public int EpisodesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the order the queue is played in.
    /// </summary>
    public PlayOrder Order { get; set; }

    /// <summary>
    /// Gets or sets the grid programs start on, in minutes. Zero - the default - runs
    /// them back to back, the way the channel always has. With 30, a program starts only
    /// on the hour or the half hour and the rest of its slot is time to fill; that is what
    /// makes "the film at 20:15" a thing the channel can actually promise.
    /// </summary>
    public int SlotMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the time a slot leaves over is filled with
    /// trailers for the program about to start, rather than left as dead air.
    /// </summary>
    public bool TrailersInGaps { get; set; } = true;

    /// <summary>
    /// Gets or sets how trailers are worked into the channel's own queue - as scheduled
    /// programming in their own right, rather than as something used to fill a gap. A gap
    /// only exists when programs start on a grid; a channel running back to back has none,
    /// and would never show a trailer at all.
    /// <para>
    /// Only trailers the library holds as files can be scheduled. A library whose trailers
    /// are links the client streams (usually YouTube) has nothing here to place, and those
    /// are left to the web client, which can embed them.
    /// </para>
    /// </summary>
    public TrailerMode Trailers { get; set; }

    /// <summary>
    /// Gets or sets how often a trailer is worked in, counted in programs. With the default
    /// of three, one plays after every third program.
    /// </summary>
    public int TrailerEveryPrograms { get; set; } = 3;

    /// <summary>
    /// Gets or sets how far ahead a trailer advertises, counted in programs. A trailer for
    /// the very next program announces rather than advertises; a few programs ahead is a
    /// preview of something still to come, which is what a trailer is for.
    /// </summary>
    public int TrailerLookahead { get; set; } = 3;

    /// <summary>
    /// Gets or sets the titles whose trailers this channel advertises regardless of what it
    /// is airing. Each entry names a movie or series, not a trailer file: the library knows
    /// which trailers belong to a title, and naming the title is what a viewer means.
    /// </summary>
    public List<ChannelSource> TrailerTitles { get; set; } = new();

    /// <summary>
    /// Gets or sets the program blocks: parts of the week that air something other than
    /// the channel's own <see cref="Sources"/>. Whatever no block covers is aired from
    /// <see cref="Sources"/>, so a channel with no blocks behaves exactly as it always
    /// has, and a channel whose blocks leave gaps falls back to its own lineup.
    /// </summary>
    public List<ProgramBlock> Blocks { get; set; } = new();

    /// <summary>
    /// Gets or sets trailers placed at particular times, rather than found for whatever the
    /// channel is about to air.
    /// <para>
    /// The automatic filling is fine for "something before the film", but it cannot say "this
    /// bumper, at eight, on weekdays". A slot claims an interstitial whose time it falls in and
    /// plays what it names - a library item, or an address, which is how a trailer the library
    /// only links to gets scheduled deliberately rather than found.
    /// </para>
    /// </summary>
    public List<TrailerSlot> TrailerSlots { get; set; } = new();

    /// <summary>
    /// Gets or sets the artwork a client draws the channel with, when the channel should not
    /// simply wear whatever is on air.
    /// <para>
    /// A channel built from one series looks after itself: what is on has a banner, and the
    /// banner is a fair picture of the channel. A channel built from a genre does not - what is
    /// on changes every hour, half of it has no wide artwork at all, and during a break there
    /// is nothing on to borrow from, which is what left "Action-Kanal" a black rectangle.
    /// </para>
    /// </summary>
    public ChannelArtwork Artwork { get; set; } = new();

}

/// <summary>
/// A part of the week that airs its own lineup: the kids' programming until noon, the
/// film on Saturday evening. A block owns a window of the day on the weekdays it applies
/// to, and holds its own sources, so the channel's identity can change with the hour
/// without needing a second channel.
/// </summary>
public class ProgramBlock
{
    /// <summary>
    /// Gets or sets the block name, shown in the guide.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the block is in use.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets when the block starts, in minutes after local midnight.
    /// </summary>
    public int StartMinutes { get; set; }

    /// <summary>
    /// Gets or sets how long the block runs, in minutes. A block may run past midnight;
    /// it then continues into the following day.
    /// </summary>
    public int DurationMinutes { get; set; } = 240;

    /// <summary>
    /// Gets or sets the local weekdays the block starts on. Empty means every day.
    /// </summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>
    /// Gets or sets the block's own content.
    /// </summary>
    public List<ChannelSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the block's source interleaving, as on the channel.
    /// </summary>
    public int EpisodesPerBlock { get; set; }

    /// <summary>
    /// Gets or sets the order the block's queue is played in.
    /// </summary>
    public PlayOrder Order { get; set; }
}

/// <summary>
/// How a channel works trailers into what it airs.
/// </summary>
public enum TrailerMode
{
    /// <summary>No trailers; the queue is programs only.</summary>
    Off,

    /// <summary>
    /// Trailers for what this channel is about to air, taken a few programs ahead so they
    /// advertise something still to come rather than announcing what is next.
    /// </summary>
    Preview,

    /// <summary>Trailers for the titles named on the channel, whatever it happens to air.</summary>
    Manual,

    /// <summary>
    /// Preview trailers where the upcoming program has one, and the channel's own titles
    /// where it does not - so a trailer slot is never left empty.
    /// </summary>
    Both
}

/// <summary>
/// The order a queue is played in.
/// </summary>
public enum PlayOrder
{
    /// <summary>Source order: each source in full, or interleaved in blocks.</summary>
    Sequential,

    /// <summary>
    /// Shuffled - but shuffled once and for good, not re-drawn on every request. A
    /// schedule that reshuffles is not a schedule: the guide would promise one thing and
    /// air another, and a viewer already watching would be moved mid-program.
    /// </summary>
    Shuffle
}

/// <summary>
/// One entry in a channel's queue: a movie, a series (expanded to all episodes in
/// chronological order) or a collection (expanded to its children by premiere date).
/// </summary>
public class ChannelSource
{
    /// <summary>
    /// Gets or sets the source type.
    /// </summary>
    public ChannelSourceType Type { get; set; }

    /// <summary>
    /// Gets or sets the library item id (movie, series or collection).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the display name at the time the source was added
    /// (config-page convenience only; the library remains authoritative).
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The kind of library item a <see cref="ChannelSource"/> references.
/// </summary>
public enum ChannelSourceType
{
    /// <summary>A single movie.</summary>
    Movie,

    /// <summary>A TV series, expanded to its episodes in aired order.</summary>
    Series,

    /// <summary>A collection (box set), expanded to its children.</summary>
    Collection
}

/// <summary>
/// Defaults that are needed before a configuration exists, or when the one on disk leaves
/// a value blank.
/// </summary>
public static class PluginConfigurationDefaults
{
    /// <summary>The account channel playback runs under when none is configured.</summary>
    public const string ChannelUserName = "LiteTV";
}

/// <summary>
/// Where a channel's own pictures come from.
/// <para>
/// Three sources, tried in the order they are written: an address, which covers anything at
/// all including an image uploaded to the server; a library item, whose artwork the channel
/// borrows; and, failing both, whatever the channel happens to be airing. The last is the
/// behaviour every channel had before this existed, so a channel that sets nothing here is
/// unchanged.
/// </para>
/// </summary>
public class ChannelArtwork
{
    /// <summary>
    /// Gets or sets the address of the wide picture drawn behind the channel in a list. Any
    /// URL the client can reach: an image on the server, or one anywhere else.
    /// </summary>
    public string? BannerUrl { get; set; }

    /// <summary>
    /// Gets or sets the address of the picture filling the screen behind the channel.
    /// </summary>
    public string? BackdropUrl { get; set; }

    /// <summary>
    /// Gets or sets the address of the channel's upright cover.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Gets or sets the library item whose artwork the channel borrows when no address is
    /// set. Naming an item rather than an image lets the channel follow it: re-scrape the
    /// series and the channel picks up the new picture with it.
    /// </summary>
    public Guid ImageItemId { get; set; }

    /// <summary>
    /// Gets or sets the name of that item, so the configuration page can say which title a
    /// channel is borrowing from without looking it up again.
    /// </summary>
    public string? ImageItemName { get; set; }
}
