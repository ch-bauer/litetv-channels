using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiteTv.Configuration;

/// <summary>
/// Plugin configuration: the channel definitions and UI options.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the channels as they used to be stored: <b>migration only, and empty on
    /// every server that has started once since.</b>
    /// <para>
    /// The channels live in <see cref="Core.ChannelStore"/> now, a file each. This property is
    /// what an older configuration document still holds, and it exists so that document can be
    /// read once and emptied. Nothing asks it what the channels are - ask the store - and
    /// <see cref="Plugin.UpdateConfiguration"/> keeps it empty so no writer can put channels
    /// back into a document where one bad value fails all of them.
    /// </para>
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
    /// Gets or sets the access token that account is currently playing with.
    /// <para>
    /// This is stored for one reason: <b>Jellyfin keeps one session per device id, so every
    /// authentication of this account revokes the token the previous holder is using.</b> The
    /// plugin used to hold the token in memory only, which meant a restart, a plugin install
    /// or a second client tuning in authenticated afresh and killed a stream that was
    /// playing - on a television, a video that loads for ever, because the stream's own
    /// requests were being refused.
    /// </para>
    /// <para>
    /// Kept here, the token outlives the process: it is checked before another is minted, and
    /// a new one is only asked for when this one is genuinely dead. It is as sensitive as
    /// <see cref="ChannelUserPassword"/> and no more - both grant the same dull account - and
    /// like the password it is never typed by anyone.
    /// </para>
    /// </summary>
    public string ChannelUserToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language the configuration page is written in: <c>auto</c>, <c>en</c>
    /// or <c>de</c>.
    /// <para>
    /// <c>auto</c> follows the dashboard's own language, which is what almost everyone wants
    /// and is therefore the default. It is a setting rather than only a guess because the
    /// dashboard's language is a per-account choice, and the owner of this server reads German
    /// while the dashboard may well be in English.
    /// </para>
    /// <para>
    /// This is the <b>page's</b> language and nothing else. What a channel is called, and what
    /// the guide says on the television, come from the library and the channel's own name.
    /// </para>
    /// </summary>
    public string PageLanguage { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the language YouTube is asked to answer in - a tag such as <c>de</c>,
    /// <c>de-DE</c> or <c>en-GB</c>. Empty follows <see cref="PageLanguage"/>, and then the
    /// server's own culture.
    /// <para>
    /// This is what a YouTube programme is CALLED in the schedule. YouTube localises titles:
    /// an uploader can give one video a German title and an English one, and the API answers
    /// with whichever the request asks for, falling back to the original where there is no
    /// translation - so asking in German costs nothing for a video that has no German title.
    /// Every call used to say <c>en</c>/<c>US</c>, hard-coded, which is why a German household
    /// read an English schedule.
    /// </para>
    /// <para>
    /// A free field rather than a list of two, because YouTube takes any tag and a shortlist
    /// here would be an answer disguised as a question. See <see cref="Trailers.YouTubeLocale"/>
    /// for how it is resolved.
    /// </para>
    /// </summary>
    public string YouTubeLanguage { get; set; } = string.Empty;

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
    /// Gets or sets the proof-of-origin token a television last minted, so it survives a
    /// restart.
    /// <para>
    /// The token is what lifts YouTube's sixty-second wall on trailer streams, and only a
    /// device with a browser engine can produce one - see
    /// <see cref="Trailers.ProofOfOrigin"/>. Holding it only in memory meant every Jellyfin
    /// restart threw it away, and a restart happens on every plugin install, so trailers went
    /// back to 360p until somebody next opened the app on a television.
    /// </para>
    /// <para>
    /// <b>Stored here, and deliberately not shown on the configuration page.</b> Configuration
    /// is the only persistence a plugin gets without inventing files, so this is where it
    /// lives - but nobody types it, so there is nothing to show. Rendering it was worse than
    /// useless: the page posts back the whole configuration as it was when it loaded, so saving
    /// a page opened before a television minted would wipe the stored token.
    /// </para>
    /// <para>
    /// <b>It is a credential of a sort.</b> Not an account - it proves a request came from
    /// something browser-shaped, not from anybody in particular, and it expires in hours - but
    /// it is worth something to whoever holds it, and it belongs nowhere public. Ask
    /// <c>GET /LiteTv/PoToken</c> whether one is held; that answers without disclosing it.
    /// </para>
    /// </summary>
    public string ProofOfOriginToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the visitor id the stored token was minted against.
    /// <para>
    /// Not optional: a token is bound to this, and one without the other is refused in a way
    /// that looks exactly like sending neither.
    /// </para>
    /// </summary>
    public string ProofOfOriginVisitorData { get; set; } = string.Empty;

    /// <summary>Gets or sets when the stored token was minted, so its age can be judged.</summary>
    public DateTime? ProofOfOriginMintedUtc { get; set; }

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
    /// Gets or sets where this channel sits in the list, counting from one.
    /// <para>
    /// A list in a document carried its own order for nothing; a folder of files does not, and
    /// "whatever order the file system enumerates in" is not an order anybody chose. So the
    /// position is written down. <see cref="Core.ChannelStore"/> sorts by it and puts a channel
    /// with none at the end, which is where somebody who has just made one looks for it.
    /// </para>
    /// </summary>
    public int Position { get; set; }

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

    /// <summary>
    /// Gets or sets a value indicating whether the block starts the next selected item on each
    /// weekly occurrence, rather than looping through its queue during the block.
    /// </summary>
    public bool AdvanceOnePerWeek { get; set; }

    /// <summary>Gets or sets whether the block length is calculated from its content.</summary>
    public bool FitToContent { get; set; } = true;

    /// <summary>Gets or sets whether this block advertises its selected programme.</summary>
    public bool TrailerEnabled { get; set; }

    /// <summary>Gets or sets how many programmes ahead the block trailer is shown.</summary>
    public int TrailerProgramsBefore { get; set; } = 3;
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

    /// <summary>
    /// Gets or sets the address, for a source that is not in the library at all - a YouTube
    /// playlist, or a single video. Empty for every other kind, where <see cref="ItemId"/>
    /// is what names the thing.
    /// <para>
    /// A playlist is expanded when the queue is built and never stored, which is the same rule
    /// the stored week follows: a playlist that gains a video reaches the channel the next time
    /// the week is laid out, rather than silently changing what a written-down schedule says.
    /// </para>
    /// </summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// What a <see cref="ChannelSource"/> references.
/// <para>
/// Every member is numbered outright. These values are written into stored configuration, so
/// inserting a member above another silently renumbers it - which would turn every existing
/// collection into something else without a word.
/// </para>
/// </summary>
public enum ChannelSourceType
{
    /// <summary>A single movie.</summary>
    Movie = 0,

    /// <summary>A TV series, expanded to its episodes in aired order.</summary>
    Series = 1,

    /// <summary>A collection (box set), expanded to its children.</summary>
    Collection = 2,

    /// <summary>
    /// A YouTube playlist or single video, named by <see cref="ChannelSource.Url"/> rather
    /// than by a library id. This is what lets a channel play something the library has never
    /// heard of.
    /// </summary>
    YouTube = 3,

    /// <summary>
    /// A single episode.
    /// <para>
    /// Needs no expansion of its own: <c>ChannelPlaylistBuilder.Expand</c> switches on the
    /// library ITEM rather than on this, so an episode falls to the same branch a film does and
    /// becomes one entry. This value exists so the page can say what it put in the list without
    /// calling an episode a film.
    /// </para>
    /// </summary>
    Episode = 4
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
