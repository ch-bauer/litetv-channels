using System.Collections.Concurrent;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Holds the set of items whose watch state must not be written, because a LiteTV channel
/// is playing them right now. <see cref="ShieldedUserDataManager"/> asks this before every
/// save, so a program that airs on a channel leaves no played flag, play count, resume
/// position or last-played date behind - the write never happens, rather than happening
/// and being undone afterwards.
/// A shield covers only the devices the channel is playing on. The same title watched
/// deliberately somewhere else records its watch state as usual, even while it happens to
/// be on air.
/// Shields are counted per playback, so a program that is started again while it is still
/// airing (the "from the beginning" button, a looping channel) stays covered until the last
/// of those playbacks has ended.
/// </summary>
public sealed class WatchStateShield
{
    /// <summary>
    /// Upper bound on how long a single armed playback can shield an item, for a client that
    /// disappears without its playback ever stopping. It has to outlast the longest program
    /// that can air, since a shield is armed when playback starts.
    /// </summary>
    private static readonly TimeSpan MaxShieldDuration = TimeSpan.FromHours(8);

    private readonly object _sync = new();

    /// <summary>Read on every user data save, so it is kept lock-free.</summary>
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), Shielded> _shielded = new();

    /// <summary>How many armed playbacks cover each item, per device.</summary>
    private readonly Dictionary<(Guid UserId, Guid ItemId), Dictionary<string, int>> _playbacks = new();

    /// <summary>What each tuned session holds, so leaving a channel releases exactly that.</summary>
    private readonly Dictionary<string, SessionShields> _bySession = new(StringComparer.Ordinal);

    /// <summary>
    /// Looks up whether an item is currently airing for a user, and on which devices.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="deviceIds">The devices the channel is playing it on. Empty when the
    /// arming session's device was unknown, which shields the item everywhere.</param>
    /// <returns><c>true</c> when the item is currently airing on a channel for that user.</returns>
    public bool TryGetShieldedDevices(Guid userId, Guid itemId, out IReadOnlyList<string> deviceIds)
    {
        deviceIds = Array.Empty<string>();

        var key = (userId, itemId);
        if (!_shielded.TryGetValue(key, out var shielded))
        {
            return false;
        }

        if (shielded.ExpiresUtc <= DateTime.UtcNow)
        {
            _shielded.TryRemove(new KeyValuePair<(Guid, Guid), Shielded>(key, shielded));
            return false;
        }

        deviceIds = shielded.DeviceIds;
        return true;
    }

    /// <summary>
    /// Covers an item for a playback that is about to start in a tuned session.
    /// </summary>
    /// <param name="sessionId">The tuned session.</param>
    /// <param name="deviceId">The device that session plays on; may be null when unknown.</param>
    /// <param name="userId">The user the playback is reported for.</param>
    /// <param name="itemId">The item about to play.</param>
    public void Arm(string sessionId, string? deviceId, Guid userId, Guid itemId)
    {
        var key = (userId, itemId);
        lock (_sync)
        {
            if (!_bySession.TryGetValue(sessionId, out var session))
            {
                _bySession[sessionId] = session = new SessionShields(deviceId);
            }

            session.Counts[key] = session.Counts.GetValueOrDefault(key) + 1;

            if (!_playbacks.TryGetValue(key, out var devices))
            {
                _playbacks[key] = devices = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            var device = session.DeviceId ?? UnknownDevice;
            devices[device] = devices.GetValueOrDefault(device) + 1;

            _shielded[key] = new Shielded(DateTime.UtcNow + MaxShieldDuration, DeviceList(devices));
        }
    }

    /// <summary>
    /// Ends one playback's cover for an item. The item stays shielded for the grace period
    /// afterwards, because progress reports the client had already sent can still be on
    /// their way when its playback stops.
    /// </summary>
    /// <param name="sessionId">The tuned session.</param>
    /// <param name="userId">The user the playback was reported for.</param>
    /// <param name="itemId">The item that stopped.</param>
    /// <param name="grace">How much longer to keep dropping writes.</param>
    public void Release(string sessionId, Guid userId, Guid itemId, TimeSpan grace)
    {
        lock (_sync)
        {
            ReleaseLocked(sessionId, (userId, itemId), 1, grace);
        }
    }

    /// <summary>
    /// Ends every cover a session still holds (the viewer left the channel).
    /// </summary>
    /// <param name="sessionId">The tuned session.</param>
    /// <param name="grace">How much longer to keep dropping writes.</param>
    public void ReleaseSession(string sessionId, TimeSpan grace)
    {
        lock (_sync)
        {
            if (!_bySession.TryGetValue(sessionId, out var session))
            {
                return;
            }

            foreach (var pair in session.Counts.ToList())
            {
                ReleaseLocked(sessionId, pair.Key, pair.Value, grace);
            }
        }
    }

    private const string UnknownDevice = "";

    private static string[] DeviceList(Dictionary<string, int> devices)
    {
        // An unknown device among them means the shield cannot be narrowed down safely, so
        // it applies everywhere: an empty list is the "all devices" case.
        return devices.ContainsKey(UnknownDevice)
            ? Array.Empty<string>()
            : devices.Keys.ToArray();
    }

    private void ReleaseLocked(string sessionId, (Guid UserId, Guid ItemId) key, int count, TimeSpan grace)
    {
        if (!_bySession.TryGetValue(sessionId, out var session) || !session.Counts.TryGetValue(key, out var held))
        {
            return; // this session does not cover the item (any more)
        }

        count = Math.Min(count, held);
        if (held - count <= 0)
        {
            session.Counts.Remove(key);
            if (session.Counts.Count == 0)
            {
                _bySession.Remove(sessionId);
            }
        }
        else
        {
            session.Counts[key] = held - count;
        }

        if (!_playbacks.TryGetValue(key, out var devices))
        {
            return;
        }

        var device = session.DeviceId ?? UnknownDevice;
        var onDevice = devices.GetValueOrDefault(device) - count;
        if (onDevice > 0)
        {
            devices[device] = onDevice;
        }
        else
        {
            devices.Remove(device);
        }

        if (devices.Count > 0)
        {
            // Still airing elsewhere; keep the shield, minus the device that stopped.
            if (_shielded.TryGetValue(key, out var current))
            {
                _shielded[key] = current with { DeviceIds = DeviceList(devices) };
            }

            return;
        }

        _playbacks.Remove(key);

        // Keep the device list through the grace period: what it still has to catch are the
        // trailing reports of the playback that just ended, which come from that same device.
        var until = DateTime.UtcNow + grace;
        if (_shielded.TryGetValue(key, out var shielded) && until < shielded.ExpiresUtc)
        {
            _shielded[key] = shielded with { ExpiresUtc = until };
        }
    }

    private sealed record Shielded(DateTime ExpiresUtc, string[] DeviceIds);

    private sealed class SessionShields
    {
        public SessionShields(string? deviceId)
        {
            DeviceId = deviceId;
        }

        public string? DeviceId { get; }

        public Dictionary<(Guid UserId, Guid ItemId), int> Counts { get; } = new();
    }
}
