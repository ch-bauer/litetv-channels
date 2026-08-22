using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Where a channel's stored week lives: one JSON file per channel, under the plugin's own
/// folder in the server configuration directory.
/// <para>
/// Deliberately not in <see cref="Configuration.PluginConfiguration"/>, and this is the
/// important part. The configuration page posts back the whole configuration as it was when
/// the page loaded, so anything held there is overwritten by a page that was opened before it
/// changed - which is how a proof-of-origin token was lost once already
/// (<see cref="Trailers.ProofOfOrigin"/>). A week is hundreds of rows that the owner edits one
/// at a time in a timeline while the rest of the page sits there getting stale; losing an
/// evening's curation to an unrelated save would be the same bug with far more to lose. So the
/// week has its own file, its own endpoints, and no way for a configuration save to touch it.
/// </para>
/// <para>
/// A file rather than rows in a database because a plugin has no database of its own, and
/// because a week is a thing the owner may reasonably want to copy, back up, or hand to
/// another channel by renaming.
/// </para>
/// </summary>
public class WeekStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly ILogger<WeekStore> _logger;
    private readonly ConcurrentDictionary<Guid, StoredWeek> _cache = new();

    // One writer at a time. Two saves racing would each write a whole file, and the loser
    // would not merely lose its own change - it would write a week that never existed.
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WeekStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="logger">The logger.</param>
    public WeekStore(IApplicationPaths applicationPaths, ILogger<WeekStore> logger)
    {
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "LiteTv", "weeks");
        _logger = logger;
    }

    /// <summary>
    /// Gets a channel's stored week, or null when it has none and is still airing whatever its
    /// sources and settings say. A channel with no week is not broken: it is one nobody has
    /// laid out yet, and the old computed schedule answers for it.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The week, or null.</returns>
    public StoredWeek? Get(Guid channelId)
    {
        if (_cache.TryGetValue(channelId, out var cached))
        {
            return cached;
        }

        var path = PathFor(channelId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var week = JsonSerializer.Deserialize<StoredWeek>(File.ReadAllText(path), SerializerOptions);
            if (week is null)
            {
                return null;
            }

            week.ChannelId = channelId;
            _cache[channelId] = week;
            return week;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A week that cannot be read is not a reason to take the channel off air: fall
            // back to the computed schedule, which is what the channel had before it was
            // curated, and say so loudly enough to be found in the log.
            _logger.LogError(ex, "LiteTV: could not read the stored week at {Path}; the channel falls back to its generated schedule", path);
            return null;
        }
    }

    /// <summary>
    /// Writes a channel's week.
    /// </summary>
    /// <param name="week">The week; its rows are normalised before they are written, so a
    /// caller may hand over whatever the page sent.</param>
    public void Save(StoredWeek week)
    {
        week.Airings = WeekEditing.Normalise(week.Airings);
        week.ModifiedUtc = DateTime.UtcNow;

        lock (_writeLock)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(week.ChannelId);

            // Written beside the real file and moved onto it, so a server that stops halfway
            // through leaves the previous week intact rather than half a week.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(week, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }

        _cache[week.ChannelId] = week;
    }

    /// <summary>
    /// Throws a channel's week away, putting it back to its generated schedule. The one thing
    /// here that loses work, so nothing calls it except the owner asking for it.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    public void Delete(Guid channelId)
    {
        lock (_writeLock)
        {
            var path = PathFor(channelId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        _cache.TryRemove(channelId, out _);
    }

    /// <summary>
    /// Gets whether a channel has a stored week at all.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>True when it does.</returns>
    public bool Has(Guid channelId) => Get(channelId) is not null;

    private string PathFor(Guid channelId)
        => Path.Combine(_directory, channelId.ToString("N") + ".json");
}
