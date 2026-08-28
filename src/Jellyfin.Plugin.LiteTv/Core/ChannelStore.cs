using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Where the channels live: <b>one JSON file per channel</b>, under the plugin's own folder in
/// the server configuration directory, beside the stored weeks.
/// <para>
/// They used to be a <c>List&lt;TvChannel&gt;</c> inside
/// <see cref="Configuration.PluginConfiguration"/>, which is what Jellyfin hands a plugin by
/// default rather than anything anybody chose. A plugin's configuration is saved as <b>one
/// document</b>, and that shape cost this project twice:
/// </para>
/// <list type="bullet">
/// <item><description><b>One bad value failed every channel.</b> A channel written with a
/// <see cref="Configuration.TrailerMode"/> that did not exist made the whole document
/// unreadable, so the server answered 500 and nothing could be saved - not the new channel and
/// not the four valid ones beside it. Creating a channel bricked the plugin.</description></item>
/// <item><description><b>A stale page overwrote what it never knew.</b> The page posts back the
/// whole configuration as it was when it loaded, which is how a proof-of-origin token was lost
/// once already - see <see cref="Plugin.UpdateConfiguration"/>, which exists only to patch that
/// hole one field at a time.</description></item>
/// </list>
/// <para>
/// A file each answers both. A channel that cannot be read takes only itself off the air and
/// says so in the log; saving one channel cannot touch another; and a page that has never heard
/// of a channel cannot delete it by omission. What is left in the configuration document is
/// what genuinely is one thing: the playback account, its token, the language.
/// </para>
/// <para>
/// The same reasoning and the same shape as <see cref="WeekStore"/>, which went this way first
/// and for the second of those two reasons. Enums are written as <b>names</b>, so a channel
/// file can be read and repaired by hand, and so an unknown one fails loudly rather than
/// landing as a number that means something else.
/// </para>
/// </summary>
public class ChannelStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly ILogger<ChannelStore> _logger;
    private readonly ConcurrentDictionary<Guid, TvChannel> _cache = new();

    // One writer at a time, as in WeekStore: two saves racing would each write a whole file.
    private readonly object _writeLock = new();

    private bool _read;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="logger">The logger.</param>
    public ChannelStore(IApplicationPaths applicationPaths, ILogger<ChannelStore> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "LiteTv", "channels");
        _logger = logger;

        HasTakenOver = MigrateFromConfiguration();
    }

    /// <summary>Raised after a channel is written or deleted, so the weeks can be tidied.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets a value indicating whether the store is the authority on what the channels are -
    /// true once there is nothing left to move out of the configuration document.
    /// <para>
    /// Read by <see cref="Plugin.UpdateConfiguration"/>, which keeps
    /// <see cref="Configuration.PluginConfiguration.Channels"/> empty from then on. It is
    /// deliberately <b>false while a migration is outstanding or has failed</b>: emptying that
    /// list before the files exist would throw the channels away, which is a far worse fault
    /// than the one being fixed.
    /// </para>
    /// </summary>
    public static bool HasTakenOver { get; private set; }

    /// <summary>
    /// Every channel, in the order the page shows them: <see cref="TvChannel.Position"/> first,
    /// and the name to settle a tie so the order is at least stable rather than whatever the
    /// file system felt like.
    /// </summary>
    /// <returns>The channels.</returns>
    public IReadOnlyList<TvChannel> All()
    {
        ReadOnce();
        return _cache.Values
            .OrderBy(c => c.Position)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Gets one channel, or null when there is no such file.</summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>The channel, or null.</returns>
    public TvChannel? Get(Guid channelId)
    {
        ReadOnce();
        return _cache.TryGetValue(channelId, out var found) ? found : null;
    }

    /// <summary>
    /// Writes one channel. A channel with no <see cref="TvChannel.Position"/> yet goes to the
    /// end, which is where somebody who has just made one expects to find it.
    /// </summary>
    /// <param name="channel">The channel.</param>
    public void Save(TvChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ReadOnce();

        if (channel.Position <= 0)
        {
            channel.Position = _cache.IsEmpty ? 1 : _cache.Values.Max(c => c.Position) + 1;
        }

        Write(channel);
        _cache[channel.Id] = channel;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Throws a channel away. Its stored week is tidied up separately.</summary>
    /// <param name="channelId">The channel.</param>
    /// <returns>True when there was one to delete.</returns>
    public bool Delete(Guid channelId)
    {
        ReadOnce();

        lock (_writeLock)
        {
            var path = PathFor(channelId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var had = _cache.TryRemove(channelId, out _);
        Changed?.Invoke(this, EventArgs.Empty);
        return had;
    }

    /// <summary>
    /// Reads the folder once, on the first question asked of the store.
    /// <para>
    /// <b>Each file is read on its own.</b> That is the entire point of the change: a channel
    /// whose JSON is broken, or which carries a value no enum here has, is logged and left out,
    /// and every other channel goes on the air.
    /// </para>
    /// </summary>
    private void ReadOnce()
    {
        if (_read)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_read)
            {
                return;
            }

            _read = true;

            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var channel = JsonSerializer.Deserialize<TvChannel>(File.ReadAllText(path), SerializerOptions);
                    if (channel is null || channel.Id == Guid.Empty)
                    {
                        _logger.LogWarning("LiteTV: {Path} holds no channel and was ignored.", path);
                        continue;
                    }

                    _cache[channel.Id] = channel;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(
                        ex,
                        "LiteTV: the channel at {Path} could not be read and is off the air; every other channel is unaffected. Fix or delete the file.",
                        path);
                }
            }
        }
    }

    /// <summary>
    /// Moves channels out of the configuration document the first time this runs, and never
    /// again.
    /// <para>
    /// The order is the careful part. Every channel is written to its own file <b>and read back
    /// again</b> before the list in the configuration is cleared; if a single one of them does
    /// not survive the round trip the list is left exactly where it is, and the plugin goes on
    /// working the old way. Losing the channels here would be a worse fault than the one this
    /// is fixing.
    /// </para>
    /// </summary>
    /// <returns>True when nothing is left in the configuration document to move.</returns>
    private bool MigrateFromConfiguration()
    {
        var plugin = Plugin.Instance;
        var carried = plugin?.Configuration.Channels;
        if (plugin is null || carried is null || carried.Count == 0)
        {
            // Nothing to move: either a fresh install, or a server that has already done this.
            return plugin is not null;
        }

        _logger.LogInformation(
            "LiteTV: moving {Count} channel(s) out of the configuration document and into a file each.",
            carried.Count);

        try
        {
            var position = 0;
            foreach (var channel in carried)
            {
                channel.Position = ++position;
                Write(channel);

                var readBack = JsonSerializer.Deserialize<TvChannel>(
                    File.ReadAllText(PathFor(channel.Id)),
                    SerializerOptions);

                if (readBack is null || readBack.Id != channel.Id)
                {
                    throw new InvalidOperationException(
                        "the channel written to " + PathFor(channel.Id) + " did not read back");
                }

                _cache[channel.Id] = channel;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The configuration is untouched, so nothing is lost; the plugin keeps reading the
            // old list until somebody has looked at this.
            _logger.LogError(ex, "LiteTV: the channels could not be moved into their own files; they stay in the configuration.");
            _cache.Clear();
            return false;
        }

        // Only now. A save of the configuration from here cannot take the channels with it,
        // because they are already somewhere else.
        plugin.Configuration.Channels.Clear();
        plugin.UpdateConfiguration(plugin.Configuration);
        _read = true;

        _logger.LogInformation("LiteTV: the channels now live in {Directory}.", _directory);
        return true;
    }

    private void Write(TvChannel channel)
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(channel.Id);

            // Beside the real file and moved onto it, so a server that stops halfway through
            // leaves the previous channel intact rather than half a channel.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(channel, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private string PathFor(Guid channelId)
        => Path.Combine(_directory, channelId.ToString("N", CultureInfo.InvariantCulture) + ".json");
}
