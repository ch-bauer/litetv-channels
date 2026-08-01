using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Tracks sessions that are tuned to a LiteTV channel and keeps channel viewing from
/// leaving traces on the account: every item the channel is about to play is registered
/// via <see cref="Tune"/> before playback starts, which snapshots the user's item data
/// (resume position, played flag, play count). The snapshot is restored while the item
/// plays, when it stops and when the viewer leaves, so Continue Watching, Next Up and
/// watched state stay untouched.
/// Only items registered that way are touched: an item the viewer starts themselves in a
/// session that is still marked tuned keeps its normal watch state.
/// For sessions tuned via the PlayOn endpoint (native clients without the injected
/// script) it additionally pushes the next scheduled item when an item plays to the end.
/// </summary>
public class TunedSessionMonitor : IHostedService
{
    private static readonly TimeSpan TunedSessionLifetime = TimeSpan.FromHours(8);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelPlaylistBuilder _playlistBuilder;
    private readonly ILogger<TunedSessionMonitor> _logger;

    private readonly ConcurrentDictionary<string, TunedSession> _tuned = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TunedSessionMonitor"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="logger">The logger.</param>
    public TunedSessionMonitor(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ChannelPlaylistBuilder playlistBuilder,
        ILogger<TunedSessionMonitor> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _playlistBuilder = playlistBuilder;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks a session as tuned to a channel and, when an item is given, snapshots that
    /// item's user data before playback starts. The injected script calls this before
    /// every item it plays, so the snapshot captures the true pre-playback state (play
    /// count and played flag are bumped around playback start, so snapshotting only at
    /// PlaybackStart would already be too late).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="followSchedule">Whether the server should push the next scheduled item
    /// when an item finishes (native clients without the injected script).</param>
    /// <param name="itemId">The item about to play, to snapshot before playback; optional.</param>
    public void Tune(string sessionId, Guid channelId, bool followSchedule, Guid? itemId = null)
    {
        // Preserve an existing session (and its snapshots) across the repeated calls the
        // script makes; only the first call establishes followSchedule.
        var tuned = _tuned.GetOrAdd(sessionId, _ => new TunedSession(channelId, followSchedule));
        tuned.Touch();

        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            var item = _libraryManager.GetItemById(itemId.Value);
            if (item is not null)
            {
                foreach (var user in GetSessionUsers(sessionId))
                {
                    SnapshotItem(tuned, user, item);
                }
            }
        }

        Prune();
    }

    /// <summary>
    /// Removes the tuned mark from a session and restores the user data of everything the
    /// channel played in it (the viewer may have left mid-item, and the server's own
    /// stop-save can land after the per-item restore).
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Untune(string sessionId)
    {
        if (_tuned.TryRemove(sessionId, out var tuned))
        {
            RestoreAll(tuned);
        }
    }

    private void RestoreAll(TunedSession tuned)
    {
        foreach (var pair in tuned.Snapshots)
        {
            var user = _userManager.GetUserById(pair.Key.UserId);
            var item = _libraryManager.GetItemById(pair.Key.ItemId);
            if (user is not null && item is not null)
            {
                _ = RestoreUserDataAsync(user, item, pair.Value);
            }
        }
    }

    private IEnumerable<User> GetSessionUsers(string sessionId)
    {
        var session = _sessionManager.Sessions
            .FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        if (session is null)
        {
            yield break;
        }

        var seen = new HashSet<Guid>();
        if (session.UserId != Guid.Empty && seen.Add(session.UserId))
        {
            var user = _userManager.GetUserById(session.UserId);
            if (user is not null)
            {
                yield return user;
            }
        }

        foreach (var additional in session.AdditionalUsers)
        {
            if (seen.Add(additional.UserId))
            {
                var user = _userManager.GetUserById(additional.UserId);
                if (user is not null)
                {
                    yield return user;
                }
            }
        }
    }

    private void SnapshotItem(TunedSession tuned, User user, MediaBrowser.Controller.Entities.BaseItem item)
    {
        var key = (user.Id, item.Id);
        if (tuned.Snapshots.ContainsKey(key))
        {
            return; // keep the oldest snapshot if the item is prepared/started more than once
        }

        try
        {
            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            tuned.Snapshots[key] = new UserDataSnapshot(
                data.PlaybackPositionTicks,
                data.Played,
                data.PlayCount,
                data.LastPlayedDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not snapshot user data for {Item}.", item.Name);
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Session is null || e.Item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        // No snapshot is taken here: by the time this fires the play count, played flag
        // and last-played date have already been bumped, so a snapshot would preserve the
        // damage instead of the original state. Everything the channel plays is registered
        // through Tune beforehand; anything else playing in this session is the viewer's
        // own doing and must keep its normal watch state.
        tuned.Touch();
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        // Every progress report the server just persisted put the item into
        // Continue Watching; revert it right away so channel viewing stays
        // invisible on the account even while it is running.
        tuned.Touch();
        foreach (var user in e.Users)
        {
            if (!tuned.Snapshots.TryGetValue((user.Id, item.Id), out var snapshot))
            {
                continue;
            }

            try
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is null || (data.PlaybackPositionTicks == snapshot.PlaybackPositionTicks && data.Played == snapshot.Played))
                {
                    continue;
                }

                data.PlaybackPositionTicks = snapshot.PlaybackPositionTicks;
                data.Played = snapshot.Played;
                data.PlayCount = snapshot.PlayCount;
                data.LastPlayedDate = snapshot.LastPlayedDate;
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiteTV: could not revert progress for {Item}.", item.Name);
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null || !_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        tuned.Touch();
        foreach (var user in e.Users)
        {
            if (!tuned.Snapshots.TryGetValue((user.Id, item.Id), out var snapshot))
            {
                continue;
            }

            // Deferred: the server's own stop-save and any trailing progress report
            // from the client would otherwise overwrite the restored state again,
            // putting the item back into Continue Watching.
            //
            // The snapshot is kept rather than removed: the same item can be started
            // again in this session ("Von Anfang an" replays it, a looping channel comes
            // back around), and its pre-channel state has to survive that. It is dropped
            // when the session is untuned.
            _ = RestoreUserDataAsync(user, item, snapshot);
        }

        if (!tuned.FollowSchedule)
        {
            // Script-driven session. The stop of the item that just ended arrives *after*
            // the script has already registered and snapshotted the follow-up item, so
            // dropping the session here would throw that snapshot away and leave every
            // item after the first one unprotected. The script sends DELETE /LiteTv/Tuned
            // when the viewer really leaves the channel.
            return;
        }

        if (e.PlayedToCompletion)
        {
            // Native-mode session finished an item: push whatever the schedule says is
            // on now (at this moment that is the follow-up item near offset zero).
            _ = PushCurrentAsync(e.Session.Id, tuned.ChannelId);
        }
        else
        {
            // Nothing pushes a follow-up here, so the viewer stopped the channel.
            Untune(e.Session.Id);
        }
    }

    private async Task RestoreUserDataAsync(User user, MediaBrowser.Controller.Entities.BaseItem item, UserDataSnapshot snapshot)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            data.PlaybackPositionTicks = snapshot.PlaybackPositionTicks;
            data.Played = snapshot.Played;
            data.PlayCount = snapshot.PlayCount;
            data.LastPlayedDate = snapshot.LastPlayedDate;
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not restore user data for {Item}.", item.Name);
        }
    }

    private async Task PushCurrentAsync(string sessionId, Guid channelId)
    {
        try
        {
            // Give the client a moment to settle after the stop report.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var channel = Plugin.Instance?.Configuration.Channels
                .FirstOrDefault(c => c.Id == channelId && c.Enabled);
            if (channel is null)
            {
                return;
            }

            var now = ScheduleResolver.Resolve(_playlistBuilder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
            if (now is null)
            {
                return;
            }

            // Snapshot before the command goes out, for the same reason the script does:
            // once playback has started the play count and played flag are already bumped.
            Tune(sessionId, channelId, followSchedule: true, now.Current.ItemId);

            await _sessionManager.SendPlayCommand(
                sessionId,
                sessionId,
                new PlayRequest
                {
                    ItemIds = new[] { now.Current.ItemId },
                    StartPositionTicks = now.OffsetTicks,
                    PlayCommand = PlayCommand.PlayNow
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not push the next scheduled item to session {SessionId}.", sessionId);
            Untune(sessionId);
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - TunedSessionLifetime;
        foreach (var pair in _tuned)
        {
            if (pair.Value.LastActivityUtc < cutoff && _tuned.TryRemove(pair.Key, out var stale))
            {
                // Dropping the session without restoring would strand whatever it still
                // holds (a client that went away without a stop report) as watched.
                RestoreAll(stale);
            }
        }
    }

    private sealed class TunedSession
    {
        public TunedSession(Guid channelId, bool followSchedule)
        {
            ChannelId = channelId;
            FollowSchedule = followSchedule;
            LastActivityUtc = DateTime.UtcNow;
        }

        public Guid ChannelId { get; }

        public bool FollowSchedule { get; }

        public DateTime LastActivityUtc { get; private set; }

        public ConcurrentDictionary<(Guid UserId, Guid ItemId), UserDataSnapshot> Snapshots { get; } = new();

        public void Touch() => LastActivityUtc = DateTime.UtcNow;
    }

    private sealed record UserDataSnapshot(long PlaybackPositionTicks, bool Played, int PlayCount, DateTime? LastPlayedDate);
}
