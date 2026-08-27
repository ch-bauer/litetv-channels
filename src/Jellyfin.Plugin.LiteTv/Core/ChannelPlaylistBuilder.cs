using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Expands a channel's sources (movies, series, collections) into the flat ordered
/// queues its schedule is built from, and assembles the schedule itself. Results are
/// cached briefly so EPG polling stays cheap; deleted items and items without a runtime
/// are skipped.
/// </summary>
public class ChannelPlaylistBuilder
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelPlaylistBuilder> _logger;
    private readonly Trailers.YouTubePlaylist? _playlists;
    private readonly ConcurrentDictionary<Guid, (DateTime BuiltUtc, string Fingerprint, IReadOnlyList<ScheduledEntry> Entries)> _cache = new();
    private readonly ConcurrentDictionary<Guid, (DateTime BuiltUtc, string Fingerprint, ChannelSchedule Schedule)> _schedules = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelPlaylistBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ChannelPlaylistBuilder(
        ILibraryManager libraryManager,
        ILogger<ChannelPlaylistBuilder> logger,
        Trailers.YouTubePlaylist? playlists = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _playlists = playlists;

        // Channel edits should be visible immediately, not after the cache TTL.
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => Invalidate();
        }
    }

    /// <summary>
    /// Gets the expanded queue for the given channel, using a short-lived cache.
    /// </summary>
    /// <param name="channel">The channel definition.</param>
    /// <returns>The ordered playable entries; may be empty.</returns>
    public IReadOnlyList<ScheduledEntry> GetEntries(TvChannel channel)
    {
        var fingerprint = Fingerprint(channel);
        if (_cache.TryGetValue(channel.Id, out var cached)
            && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
            && DateTime.UtcNow - cached.BuiltUtc < CacheTtl)
        {
            return cached.Entries;
        }

        var entries = Build(channel);
        _cache[channel.Id] = (DateTime.UtcNow, fingerprint, entries);
        return entries;
    }

    /// <summary>
    /// Gets the channel's whole schedule: the week it repeats, the lineup each program
    /// block airs, and the channel's own lineup filling everything no block claims.
    /// </summary>
    /// <param name="channel">The channel definition.</param>
    /// <returns>The schedule.</returns>
    public ChannelSchedule GetSchedule(TvChannel channel)
    {
        var fingerprint = Fingerprint(channel);
        if (_schedules.TryGetValue(channel.Id, out var cached)
            && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
            && DateTime.UtcNow - cached.BuiltUtc < CacheTtl)
        {
            return cached.Schedule;
        }

        var schedule = BuildSchedule(channel);
        _schedules[channel.Id] = (DateTime.UtcNow, fingerprint, schedule);
        return schedule;
    }

    /// <summary>
    /// Everything about a channel that decides what it airs, as one string. A cached
    /// schedule is only reused while this still matches, which is what makes an edit take
    /// effect the moment it is saved.
    /// <para>
    /// There is an event for configuration changes and this used to rely on it. It does not
    /// arrive - a channel edited in the dashboard went on airing the old schedule until the
    /// cache aged out minutes later, which reads as the setting having been ignored. Asking
    /// the configuration itself whether it changed cannot miss, and costs a string compare.
    /// </para>
    /// </summary>
    /// <param name="channel">The channel definition.</param>
    /// <returns>The fingerprint.</returns>
    internal static string Fingerprint(TvChannel channel)
    {
        var text = new System.Text.StringBuilder();
        text.Append(channel.AnchorUtc.Ticks).Append('|')
            .Append(channel.EpisodesPerBlock).Append('|')
            .Append((int)channel.Order).Append('|')
            .Append(channel.SlotMinutes).Append('|')
            .Append(channel.TrailersInGaps).Append('|')
            .Append((int)channel.Trailers).Append('|')
            .Append(channel.TrailerEveryPrograms).Append('|')
            .Append(channel.TrailerLookahead).Append('|');
        AppendSources(text, channel.Sources);
        text.Append('~');
        AppendSources(text, channel.TrailerTitles);

        foreach (var block in channel.Blocks)
        {
            text.Append("#").Append(block.Name).Append(',')
                .Append(block.Enabled).Append(',')
                .Append(block.StartMinutes).Append(',')
                .Append(block.DurationMinutes).Append(',')
                .Append(string.Join('+', block.Days)).Append(',')
                .Append(block.EpisodesPerBlock).Append(',')
                .Append((int)block.Order).Append(':');
            AppendSources(text, block.Sources);
        }

        return text.ToString();
    }

    private static void AppendSources(System.Text.StringBuilder text, IReadOnlyList<ChannelSource> sources)
    {
        foreach (var source in sources)
        {
            // The address is part of what a source IS, not decoration: a YouTube source has no
            // item id at all, so without this every playlist fingerprints the same and swapping
            // one for another would go on airing the old queue until the cache aged out.
            text.Append(source.ItemId.ToString("N")).Append('-')
                .Append((int)source.Type).Append('-')
                .Append(source.Url).Append(';');
        }
    }

    /// <summary>
    /// Gets the trailers held in the library for an item, as things a channel can actually
    /// air. Only local ones: a trailer that lives on YouTube is not a file the server can
    /// schedule, so those are left to the web client, which can embed them.
    /// </summary>
    /// <param name="itemId">The library item.</param>
    /// <returns>The trailers, longest last; empty when the library has none.</returns>
    public IReadOnlyList<ScheduledEntry> TrailersFor(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Array.Empty<ScheduledEntry>();
        }

        var trailers = new List<ScheduledEntry>();
        foreach (var extra in item.GetExtras(new[] { ExtraType.Trailer, ExtraType.Clip }))
        {
            if ((extra.RunTimeTicks ?? 0) > 0)
            {
                trailers.Add(new ScheduledEntry(extra.Id, extra.Name ?? string.Empty, item.Name, item.Id, extra.RunTimeTicks!.Value));
            }
        }

        return trailers;
    }

    /// <summary>
    /// How long a library item runs, in ticks, or zero when it is unknown or gone.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The runtime in ticks.</returns>
    public long RuntimeOf(Guid itemId)
        => _libraryManager.GetItemById(itemId)?.RunTimeTicks ?? 0;

    /// <summary>
    /// What a library item is called, or an empty string when it is gone. An episode is named
    /// as the series and the episode together, because "Folge 3" on its own in a schedule tells
    /// nobody what channel they are looking at.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The name.</returns>
    public string NameOf(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return string.Empty;
        }

        return item is MediaBrowser.Controller.Entities.TV.Episode episode
            && !string.IsNullOrEmpty(episode.SeriesName)
                ? $"{episode.SeriesName} - {item.Name}"
                : item.Name ?? string.Empty;
    }

    /// <summary>
    /// Whether anything could actually be played to advertise this programme: a trailer file
    /// the library holds, or a link a metadata provider attached.
    /// <para>
    /// Asked before a break is labelled as trailing something. A schedule that announces
    /// "Vorschau: Avatar" and then has nothing to play spends the break in silence and tells
    /// the viewer about a preview that does not exist - twice over, since the guide prints it
    /// too. An episode's trailers live on its series, the same fallback the resolving endpoint
    /// makes, because nobody cuts a trailer per episode.
    /// </para>
    /// </summary>
    /// <param name="itemId">The programme.</param>
    /// <returns>Whether it has a trailer of any kind.</returns>
    public bool HasTrailer(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        if (TrailersFor(itemId).Count > 0)
        {
            return true;
        }

        if (item.RemoteTrailers.Any(t => !string.IsNullOrEmpty(t.Url)))
        {
            return true;
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != Guid.Empty)
        {
            var series = _libraryManager.GetItemById(episode.SeriesId);
            return series?.RemoteTrailers.Any(t => !string.IsNullOrEmpty(t.Url)) == true;
        }

        return false;
    }

    /// <summary>
    /// The address of a programme's linked trailer, where it has one. The library holds far
    /// more trailers as links than as files, and a link is the case the schedule knows least
    /// about - so this is what lets the length of one be looked up.
    /// </summary>
    /// <param name="itemId">The programme.</param>
    /// <returns>The address, or null.</returns>
    public string? RemoteTrailerUrl(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var url = item.RemoteTrailers.FirstOrDefault(t => !string.IsNullOrEmpty(t.Url))?.Url;
        if (!string.IsNullOrEmpty(url))
        {
            return url;
        }

        // An episode's trailers live on its series - nobody cuts one per episode - which is
        // the same fallback HasTrailer makes.
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != Guid.Empty)
        {
            return _libraryManager.GetItemById(episode.SeriesId)?
                .RemoteTrailers.FirstOrDefault(t => !string.IsNullOrEmpty(t.Url))?.Url;
        }

        return null;
    }

    /// <summary>
    /// Which decade a library item was made in - 1980 for the eighties - or zero when nothing
    /// says. Used to put an advert of the right vintage in front of it.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The decade, or zero.</returns>
    public int DecadeOf(Guid itemId)
    {
        var year = _libraryManager.GetItemById(itemId)?.ProductionYear ?? 0;
        return year > 0 ? year / 10 * 10 : 0;
    }

    /// <summary>
    /// Drops all cached queues and schedules (called when the plugin configuration changes).
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
        _schedules.Clear();
    }

    private ChannelSchedule BuildSchedule(TvChannel channel)
    {
        var slotTicks = Math.Max(0, channel.SlotMinutes) * TimeSpan.TicksPerMinute;
        var lineups = new Dictionary<int, Lineup>
        {
            [WeekTimeline.BaseLineup] = new Lineup(GetEntries(channel), slotTicks)
        };
        var names = new Dictionary<int, string>();
        var windows = new List<BlockWindow>();

        for (var i = 0; i < channel.Blocks.Count; i++)
        {
            var block = channel.Blocks[i];
            if (!block.Enabled || block.DurationMinutes <= 0)
            {
                continue;
            }

            windows.Add(new BlockWindow(i, block.StartMinutes, block.DurationMinutes, block.Days));
            names[i] = block.Name;
            lineups[i] = new Lineup(
                Order(Interleave(Expand(block.Sources, channel.Name), block.EpisodesPerBlock), block.Order, channel.Id, i),
                slotTicks);
        }

        return new ChannelSchedule(
            WeekTimeline.Build(windows),
            lineups,
            names,
            channel.AnchorUtc,
            TimeZoneInfo.Local);
    }

    private IReadOnlyList<ScheduledEntry> Build(TvChannel channel)
    {
        return WithScheduledTrailers(
            Order(
                Interleave(Expand(channel.Sources, channel.Name), channel.EpisodesPerBlock),
                channel.Order,
                channel.Id,
                WeekTimeline.BaseLineup),
            channel);
    }

    /// <summary>
    /// Works trailers into a channel's queue as programming in their own right.
    /// <para>
    /// A trailer here is not filler. Filling gaps only ever happens where a slot grid leaves
    /// one, so a channel running back to back would never show a trailer at all - and a
    /// trailer for whatever starts in a moment announces rather than advertises. So one is
    /// worked in every few programs, for something far enough ahead to still be worth
    /// hearing about.
    /// </para>
    /// <para>
    /// The choice is made from the queue and the channel alone, never from the clock: a
    /// trailer becomes a scheduled entry like any other, and everything after it in the loop
    /// moves by its length. Were it drawn any other way the guide would promise one thing
    /// and the channel would air another.
    /// </para>
    /// <para>
    /// Only trailers the library holds as files can be placed. Where a library links its
    /// trailers out instead, nothing is scheduled and the web client embeds them as before.
    /// </para>
    /// </summary>
    /// <param name="queue">The channel's programs, in the order they play.</param>
    /// <param name="channel">The channel definition.</param>
    /// <returns>The queue with trailers worked in.</returns>
    private IReadOnlyList<ScheduledEntry> WithScheduledTrailers(IReadOnlyList<ScheduledEntry> queue, TvChannel channel)
    {
        if (channel.Trailers == TrailerMode.Off || queue.Count == 0)
        {
            return queue;
        }

        var every = Math.Max(1, channel.TrailerEveryPrograms);
        var lookahead = Math.Max(1, channel.TrailerLookahead);
        var wantsPreview = channel.Trailers is TrailerMode.Preview or TrailerMode.Both;
        var wantsManual = channel.Trailers is TrailerMode.Manual or TrailerMode.Both;

        var named = wantsManual
            ? channel.TrailerTitles.SelectMany(t => TrailersFor(t.ItemId)).ToList()
            : new List<ScheduledEntry>();

        var result = new List<ScheduledEntry>(queue.Count);
        var namedTaken = 0;

        for (var i = 0; i < queue.Count; i++)
        {
            result.Add(queue[i]);

            if ((i + 1) % every != 0)
            {
                continue;
            }

            ScheduledEntry? trailer = null;
            if (wantsPreview)
            {
                // Wrapping is deliberate: a channel is a loop, so the program a few places on
                // from the end is the one that comes round at the start again.
                var advertised = queue[(i + lookahead) % queue.Count];
                trailer = TrailersFor(advertised.ItemId).FirstOrDefault();
            }

            if (trailer is null && named.Count > 0)
            {
                trailer = named[namedTaken++ % named.Count];
            }

            if (trailer is not null)
            {
                result.Add(trailer);
            }
        }

        return result;
    }

    /// <summary>
    /// Expands each source into its own ordered list ("stream"). With the default block
    /// size the streams are simply concatenated (each source played in full); with a
    /// positive block size they are interleaved round-robin.
    /// </summary>
    private List<List<ScheduledEntry>> Expand(IReadOnlyList<ChannelSource> sources, string channelName)
    {
        var streams = new List<List<ScheduledEntry>>();
        foreach (var source in sources)
        {
            if (source.Type == ChannelSourceType.YouTube)
            {
                var fromYouTube = FromYouTube(source, channelName);
                if (fromYouTube.Count > 0)
                {
                    streams.Add(fromYouTube);
                }

                continue;
            }

            var item = _libraryManager.GetItemById(source.ItemId);
            if (item is null)
            {
                _logger.LogWarning("LiteTV channel {Channel}: source item {ItemId} no longer exists; skipping.", channelName, source.ItemId);
                continue;
            }

            var stream = new List<ScheduledEntry>();
            switch (item)
            {
                case Series series:
                    AddSeries(stream, series);
                    break;
                case BoxSet boxSet:
                    AddCollection(stream, boxSet);
                    break;
                default:
                    AddIfPlayable(stream, item);
                    break;
            }

            if (stream.Count > 0)
            {
                streams.Add(stream);
            }
        }

        return streams;
    }

    /// <summary>
    /// Expands a YouTube source into playable entries.
    /// <para>
    /// A playlist becomes its videos, in playlist order; a single video becomes itself. The
    /// list is read <b>now</b>, while the queue is being built, and never stored - the same rule
    /// the stored week follows.
    /// </para>
    /// <para>
    /// A video YouTube gives no length for is <b>dropped</b>, not guessed at. A schedule is a
    /// promise about when things start, and one entry of made-up length moves everything after
    /// it - which is the same reasoning that removed the typed-length box twice already.
    /// </para>
    /// </summary>
    /// <param name="source">The source, whose Url names a playlist or a video.</param>
    /// <param name="channelName">The channel, for the log.</param>
    /// <returns>The entries, in order.</returns>
    private List<ScheduledEntry> FromYouTube(ChannelSource source, string channelName)
    {
        var entries = new List<ScheduledEntry>();

        if (_playlists is null || string.IsNullOrWhiteSpace(source.Url))
        {
            _logger.LogWarning(
                "LiteTV channel {Channel}: a YouTube source has no address, or no reader is available.",
                channelName);
            return entries;
        }

        // Built synchronously because everything that asks for a queue does; the answer is
        // cached by the builder, so this runs when the lineup changes rather than per request.
        var items = _playlists.ItemsAsync(source.Url, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var skipped = 0;
        foreach (var item in items)
        {
            if (item.Seconds <= 0)
            {
                skipped++;
                continue;
            }

            entries.Add(new ScheduledEntry(
                Guid.Empty,
                item.Title,
                source.Name.Length > 0 ? source.Name : null,
                null,
                TimeSpan.FromSeconds(item.Seconds).Ticks)
            {
                Url = item.Url
            });
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "LiteTV channel {Channel}: {Skipped} of {Total} videos in {Source} had no length and were left out.",
                channelName,
                skipped,
                items.Count,
                source.Name.Length > 0 ? source.Name : source.Url);
        }

        return entries;
    }

    /// <summary>
    /// Applies the channel's play order. A shuffle is drawn from the channel and lineup
    /// alone, never from the clock, so it is the same shuffle on every request, on every
    /// client and after every restart. A schedule that re-draws is not a schedule: the
    /// guide would promise one program and the player would air another.
    /// </summary>
    private static IReadOnlyList<ScheduledEntry> Order(
        IReadOnlyList<ScheduledEntry> entries,
        PlayOrder order,
        Guid channelId,
        int owner)
    {
        if (order != PlayOrder.Shuffle || entries.Count < 2)
        {
            return entries;
        }

        // Deliberately not System.Random: a seeded shuffle only holds still as long as the
        // generator behind it does, and this one has to hold across runtime versions.
        var state = Seed(channelId, owner);
        var shuffled = entries.ToList();
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            state = NextState(state);
            var j = (int)(state % (ulong)(i + 1));
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }

    private static ulong Seed(Guid channelId, int owner)
    {
        Span<byte> bytes = stackalloc byte[16];
        channelId.TryWriteBytes(bytes);
        ulong seed = 1469598103934665603;
        foreach (var b in bytes)
        {
            seed = (seed ^ b) * 1099511628211;
        }

        return seed ^ ((ulong)owner + 0x9E3779B97F4A7C15);
    }

    /// <summary>xorshift64*, chosen because it is a handful of operations that will mean the
    /// same thing in ten years.</summary>
    private static ulong NextState(ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state == 0 ? 0x9E3779B97F4A7C15 : state;
    }

    /// <summary>
    /// Merges the per-source streams. A non-positive block size concatenates them in
    /// order (each source in full). A positive block size rotates through the sources
    /// taking up to that many items from each per round, so multiple series air in
    /// alternating blocks, e.g. block 2 over [S1, S2] gives S1E1, S1E2, S2E1, S2E2,
    /// S1E3, S1E4, ... Uneven streams simply drop out once exhausted.
    /// </summary>
    private static IReadOnlyList<ScheduledEntry> Interleave(List<List<ScheduledEntry>> streams, int blockSize)
    {
        if (blockSize <= 0 || streams.Count <= 1)
        {
            return streams.SelectMany(s => s).ToList();
        }

        var result = new List<ScheduledEntry>();
        var cursors = new int[streams.Count];
        bool progressed;
        do
        {
            progressed = false;
            for (var i = 0; i < streams.Count; i++)
            {
                var stream = streams[i];
                for (var k = 0; k < blockSize && cursors[i] < stream.Count; k++)
                {
                    result.Add(stream[cursors[i]++]);
                    progressed = true;
                }
            }
        }
        while (progressed);

        return result;
    }

    private void AddSeries(List<ScheduledEntry> entries, Series series)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = new[] { series.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        }).OfType<Episode>();

        foreach (var episode in InAiredOrder(episodes))
        {
            AddIfPlayable(entries, episode, series);
        }
    }

    /// <summary>
    /// Puts a series' episodes into the order they air in: by season, then by episode
    /// number, with specials where they belong rather than dropped or heaped at the end.
    /// A special that carries the metadata for it - "airs before S2E1", "airs after season
    /// 1" - is placed there, which is the whole point of that metadata: a recap or a
    /// prologue only makes sense next to the episode it was made for. A special with
    /// nothing to place it by airs after the numbered seasons.
    /// Specials are never dropped. Dropping them took a channel off the air completely when
    /// specials were all a series had - it expanded to nothing, and a channel with nothing
    /// to play shows "Sendepause". Season 0 is also where the server files every episode it
    /// cannot place, so "only specials" is a thing an ordinary library really does have.
    /// The path breaks ties so the order is the same every time the queue is rebuilt; two
    /// files claiming the same episode number would otherwise swap places between rebuilds
    /// and move the whole schedule under the viewer.
    /// </summary>
    /// <param name="episodes">The episodes to order.</param>
    /// <returns>The episodes in airing order.</returns>
    internal static IEnumerable<Episode> InAiredOrder(IEnumerable<Episode> episodes)
    {
        return episodes
            .OrderBy(e => AiringKey(e).Season)
            .ThenBy(e => AiringKey(e).Episode)
            .ThenBy(e => AiringKey(e).Rank)
            .ThenBy(e => e.IndexNumber ?? 0)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where an episode sits in the airing order. The rank orders the three things that can
    /// share one season/episode position: a special that airs before the episode, the
    /// episode itself, and a special that airs after a whole season.
    /// </summary>
    private static (int Season, int Episode, int Rank) AiringKey(Episode episode)
    {
        var season = episode.ParentIndexNumber ?? 0;
        if (season > 0)
        {
            return (season, episode.IndexNumber ?? 0, 1);
        }

        // A special: placed by what its metadata says it airs around.
        if (episode.AirsBeforeSeasonNumber is { } beforeSeason)
        {
            return (beforeSeason, episode.AirsBeforeEpisodeNumber ?? 0, 0);
        }

        if (episode.AirsAfterSeasonNumber is { } afterSeason)
        {
            return (afterSeason, int.MaxValue, 2);
        }

        return (int.MaxValue, episode.IndexNumber ?? 0, 1);
    }

    private void AddCollection(List<ScheduledEntry> entries, BoxSet boxSet)
    {
        var children = boxSet.GetLinkedChildren()
            .OrderBy(c => c.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase);

        foreach (var child in children)
        {
            if (child is Series series)
            {
                AddSeries(entries, series);
            }
            else
            {
                AddIfPlayable(entries, child);
            }
        }
    }

    private void AddIfPlayable(List<ScheduledEntry> entries, BaseItem item, Series? series = null)
    {
        var runtime = item.RunTimeTicks ?? 0;
        if (runtime <= 0)
        {
            _logger.LogDebug("LiteTV: item {Name} has no runtime; skipping.", item.Name);
            return;
        }

        var seriesForItem = series ?? (item as Episode)?.Series;
        entries.Add(new ScheduledEntry(
            item.Id,
            item.Name ?? string.Empty,
            seriesForItem?.Name,
            seriesForItem?.Id,
            runtime));
    }
}
