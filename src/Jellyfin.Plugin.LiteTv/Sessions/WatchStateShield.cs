using System.Collections.Concurrent;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Holds the set of items whose watch state must not be written, because a LiteTV channel
/// is playing them right now. <see cref="ShieldedUserDataManager"/> asks this before every
/// save, so a program that airs on a channel leaves no played flag, play count, resume
/// position or last-played date behind - the write never happens, rather than happening
/// and being undone afterwards.
/// Shields are counted per playback, so a program that is started again while it is still
/// airing (the "from the beginning" button, a looping channel) stays covered until the last
/// of those playbacks has ended.
/// </summary>
public sealed class WatchStateShield
{
    /// <summary>
    /// Upper bound on how long a single armed playback can shield an item. A client that
    /// disappears without reporting a stop would otherwise shield it forever, which would
    /// silently swallow the viewer's own watching of that item later on.
    /// </summary>
    private static readonly TimeSpan MaxShieldDuration = TimeSpan.FromHours(8);

    private readonly object _sync = new();

    /// <summary>Read on every user data save, so it is kept lock-free.</summary>
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), DateTime> _expiry = new();

    /// <summary>How many armed playbacks currently cover each item.</summary>
    private readonly Dictionary<(Guid UserId, Guid ItemId), int> _playbacks = new();

    /// <summary>What each tuned session holds, so leaving a channel releases exactly that.</summary>
    private readonly Dictionary<string, Dictionary<(Guid UserId, Guid ItemId), int>> _bySession
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a value indicating whether writes to this user's data for this item must be
    /// dropped.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the item is currently airing on a channel for that user.</returns>
    public bool IsShielded(Guid userId, Guid itemId)
    {
        var key = (userId, itemId);
        if (!_expiry.TryGetValue(key, out var expiresUtc))
        {
            return false;
        }

        if (expiresUtc > DateTime.UtcNow)
        {
            return true;
        }

        _expiry.TryRemove(new KeyValuePair<(Guid, Guid), DateTime>(key, expiresUtc));
        return false;
    }

    /// <summary>
    /// Covers an item for a playback that is about to start in a tuned session.
    /// </summary>
    /// <param name="sessionId">The tuned session.</param>
    /// <param name="userId">The user the playback is reported for.</param>
    /// <param name="itemId">The item about to play.</param>
    public void Arm(string sessionId, Guid userId, Guid itemId)
    {
        var key = (userId, itemId);
        lock (_sync)
        {
            _playbacks[key] = _playbacks.GetValueOrDefault(key) + 1;

            if (!_bySession.TryGetValue(sessionId, out var held))
            {
                _bySession[sessionId] = held = new Dictionary<(Guid, Guid), int>();
            }

            held[key] = held.GetValueOrDefault(key) + 1;
            _expiry[key] = DateTime.UtcNow + MaxShieldDuration;
        }
    }

    /// <summary>
    /// Ends one playback's cover for an item. The item stays shielded for the grace period
    /// afterwards: the client's final playback report is sent while its player is torn down
    /// and regularly arrives after the item has already stopped.
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
            if (!_bySession.TryGetValue(sessionId, out var held))
            {
                return;
            }

            foreach (var pair in held.ToList())
            {
                ReleaseLocked(sessionId, pair.Key, pair.Value, grace);
            }
        }
    }

    private void ReleaseLocked(string sessionId, (Guid UserId, Guid ItemId) key, int count, TimeSpan grace)
    {
        if (!_bySession.TryGetValue(sessionId, out var held) || !held.TryGetValue(key, out var sessionCount))
        {
            return; // this session does not cover the item (any more)
        }

        count = Math.Min(count, sessionCount);
        if (sessionCount - count <= 0)
        {
            held.Remove(key);
            if (held.Count == 0)
            {
                _bySession.Remove(sessionId);
            }
        }
        else
        {
            held[key] = sessionCount - count;
        }

        var remaining = _playbacks.GetValueOrDefault(key) - count;
        if (remaining > 0)
        {
            _playbacks[key] = remaining;
            return; // still airing somewhere
        }

        _playbacks.Remove(key);

        var until = DateTime.UtcNow + grace;
        if (_expiry.TryGetValue(key, out var expiresUtc) && until < expiresUtc)
        {
            _expiry[key] = until;
        }
    }
}
