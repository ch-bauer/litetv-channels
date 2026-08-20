using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiteTv.Channels;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Tracks sessions that are tuned to a LiteTV channel and keeps channel viewing off the
/// account: every item a channel is about to play is registered through <see cref="Tune"/>
/// before playback starts, which shields it in the <see cref="WatchStateShield"/> so none
/// of its watch state is ever written. What a tuned session is airing stays shielded until
/// the viewer leaves the channel - not until the item stops playing, because a stop is not
/// the end of anything: a seek, a re-prepare after the library scan touched the file, or a
/// player rebuilding itself all stop and start again, and a shield dropped in between leaves
/// the rest of the programme recording as ordinary watching.
/// Only registered items are covered: an item the viewer starts themselves in a session
/// that is still marked tuned records its watch state as usual.
/// For sessions tuned via the PlayOn endpoint (native clients without the injected script)
/// this also pushes the next scheduled item when an item plays to the end.
/// </summary>
public class TunedSessionMonitor : IHostedService
{
    private static readonly TimeSpan TunedSessionLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// How long an item stays shielded after its playback stopped. Only trailing reports the
    /// client had already sent are still on their way at that point, so this is short: the
    /// viewer may well want to watch that same program properly straight after leaving the
    /// channel, and until this elapses that would not be recorded.
    /// </summary>
    private static readonly TimeSpan ReleaseGracePeriod = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a session that has been left keeps its shields when no stop ever arrives for
    /// what it was playing. Leaving is reported before the client tears its player down, so
    /// the stop normally follows within a moment and releases the item on its own; this only
    /// catches a client that went away without saying anything.
    /// </summary>
    private static readonly TimeSpan LeftChannelTimeout = TimeSpan.FromSeconds(60);

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchStateShield _shield;
    private readonly LiveOffsetResolver _liveOffset;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<TunedSessionMonitor> _logger;

    private readonly ConcurrentDictionary<string, TunedSession> _tuned = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TunedSessionMonitor"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager, to report at startup whether the
    /// watch-state shield is in place.</param>
    /// <param name="shield">The watch-state shield.</param>
    /// <param name="liveOffset">Resolves published channel items and their live position.</param>
    /// <param name="channelManager">Used to build the entry for the program coming up.</param>
    /// <param name="logger">The logger.</param>
    public TunedSessionMonitor(
        ISessionManager sessionManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchStateShield shield,
        LiveOffsetResolver liveOffset,
        IChannelManager channelManager,
        ILogger<TunedSessionMonitor> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _shield = shield;
        _liveOffset = liveOffset;
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The shield only works if the plugin's service registration replaced the server's
        // user data manager. That depends on plugin registrations running after the
        // server's own, so say plainly in the log which way it went instead of leaving it
        // to be discovered in someone's watch history.
        if (_userDataManager is ShieldedUserDataManager)
        {
            _logger.LogInformation("LiteTV: watch-state shield active, channel viewing will not be recorded.");
        }
        else
        {
            _logger.LogError(
                "LiteTV: watch-state shield NOT active ({Type} was not wrapped), channel viewing will be recorded as normal watching.",
                _userDataManager.GetType().Name);
        }

        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded += OnSessionEnded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
        return Task.CompletedTask;
    }

    /// <summary>
    /// A client that disappears - tab killed, app force-quit, network gone - never reports a
    /// stop, so nothing would release what it was airing until the safety cap hours later.
    /// The session going away is the server noticing on its behalf.
    /// </summary>
    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        var sessionId = e.SessionInfo?.Id;
        if (!string.IsNullOrEmpty(sessionId) && _tuned.TryRemove(sessionId, out _))
        {
            _shield.ReleaseSession(sessionId, ReleaseGracePeriod);
        }
    }

    /// <summary>
    /// Marks a session as tuned to a channel and shields the item it is about to play.
    /// The injected script calls this before every item, and the server does the same
    /// before it pushes one to a native client, so the shield is always in place before
    /// the first playback report can arrive.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="followSchedule">Whether the server should push the next scheduled item
    /// when an item finishes (native clients without the injected script).</param>
    /// <param name="itemId">The item about to play; optional.</param>
    /// <param name="requestUserId">The user the tune request authenticated as, used when the
    /// session itself cannot be found; <see cref="Guid.Empty"/> when unknown.</param>
    /// <param name="requestDeviceId">The device the tune request came from, used when the
    /// session itself cannot be found.</param>
    /// <returns><c>true</c> when there is nothing left to shield or the item was shielded;
    /// <c>false</c> when it could not be, and playing it would record it.</returns>
    public bool Tune(
        string sessionId,
        Guid channelId,
        bool followSchedule,
        Guid? itemId = null,
        Guid requestUserId = default,
        string? requestDeviceId = null)
        => Tune(
            sessionId,
            channelId,
            followSchedule,
            itemId.HasValue ? new[] { itemId.Value } : Array.Empty<Guid>(),
            requestUserId,
            requestDeviceId);

    /// <summary>
    /// Marks a session as tuned and shields a whole run of items at once.
    /// <para>
    /// A client that hands the player a queue rather than one programme at a time has no
    /// later moment to arm anything: the player moves on by itself, and the first watch-state
    /// report of the next item arrives before any client code could react to it. So the queue
    /// is shielded in full up front, and released together when the viewer leaves.
    /// </para>
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="followSchedule">Whether the server pushes follow-up items.</param>
    /// <param name="itemIds">The items to shield; may be empty.</param>
    /// <param name="requestUserId">The user the request authenticated as.</param>
    /// <param name="requestDeviceId">The device the request came from.</param>
    /// <returns><c>true</c> when every item asked for is shielded.</returns>
    public bool Tune(
        string sessionId,
        Guid channelId,
        bool followSchedule,
        IReadOnlyList<Guid> itemIds,
        Guid requestUserId = default,
        string? requestDeviceId = null)
    {
        // Preserve an existing session across the repeated calls the script makes; only
        // the first call establishes followSchedule.
        var tuned = _tuned.GetOrAdd(sessionId, _ => new TunedSession(channelId, followSchedule));
        tuned.Touch();
        tuned.MarkTuned();

        Prune();

        var wanted = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return true;
        }

        var session = FindSession(sessionId);
        var userIds = GetSessionUserIds(session).ToList();

        // The session list is the server's own record of connected clients, and a client can
        // be missing from it - dropped after a reconnect, pruned, or simply never matched
        // because the id the caller resolved has gone stale. That used to mean nothing was
        // armed at all, without a word in the log, and every programme the channel then
        // played was recorded as deliberate watching. The requesting account is the one
        // being protected, so it stands in.
        if (userIds.Count == 0 && requestUserId != Guid.Empty && _userManager.GetUserById(requestUserId) is not null)
        {
            userIds.Add(requestUserId);
        }

        if (userIds.Count == 0)
        {
            _logger.LogError(
                "LiteTV: could not arm the watch-state shield for session {SessionId} - no user could be resolved, so {Count} item(s) are NOT being played.",
                sessionId,
                wanted.Count);
            return false;
        }

        // The device is what tells this playback apart from the same title watched
        // deliberately elsewhere, which must keep its watch state. Unknown means the shield
        // cannot be narrowed down, and it then covers every device - the safe way round.
        var deviceId = session?.DeviceId ?? requestDeviceId;
        foreach (var userId in userIds)
        {
            foreach (var id in wanted)
            {
                _shield.Arm(sessionId, deviceId, userId, id);
            }
        }

        _logger.LogInformation(
            "LiteTV: shielded {Count} item(s) for {Users} user(s) on device {Device}, session {SessionId}.",
            wanted.Count,
            userIds.Count,
            deviceId ?? "(any)",
            sessionId);

        return true;
    }

    /// <summary>
    /// Ends channel viewing in a session.
    /// Anything whose playback has already stopped is released by that stop, so usually
    /// nothing is left here. What can be left is the program still on air: leaving is
    /// reported before the client tears its player down, so its stop is still to come and
    /// releasing right now would let the final report through - that is what used to leave
    /// the program the viewer just left sitting in Continue Watching. The session is
    /// therefore kept just long enough to catch that stop, and force-released if none comes.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Untune(string sessionId)
    {
        if (!_tuned.TryGetValue(sessionId, out var tuned))
        {
            _shield.ReleaseSession(sessionId, ReleaseGracePeriod);
            return;
        }

        tuned.MarkLeft();
        _ = ReleaseWhenPlaybackEndedAsync(sessionId, tuned);
    }

    private async Task ReleaseWhenPlaybackEndedAsync(string sessionId, TunedSession tuned)
    {
        await Task.Delay(LeftChannelTimeout).ConfigureAwait(false);

        if (!tuned.HasLeft)
        {
            return; // tuned in again in the meantime
        }

        if (_tuned.TryGetValue(sessionId, out var current) && ReferenceEquals(current, tuned))
        {
            _tuned.TryRemove(new KeyValuePair<string, TunedSession>(sessionId, tuned));
        }

        _shield.ReleaseSession(sessionId, ReleaseGracePeriod);
    }

    private SessionInfo? FindSession(string sessionId)
    {
        return _sessionManager.Sessions
            .FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The users a session reports playback for: whoever is signed in, plus any guests
    /// watching along, since the server records the item for each of them.
    /// </summary>
    private IEnumerable<Guid> GetSessionUserIds(SessionInfo? session)
    {
        if (session is null)
        {
            yield break;
        }

        var seen = new HashSet<Guid>();
        if (session.UserId != Guid.Empty && seen.Add(session.UserId) && _userManager.GetUserById(session.UserId) is not null)
        {
            yield return session.UserId;
        }

        foreach (var additional in session.AdditionalUsers)
        {
            if (seen.Add(additional.UserId) && _userManager.GetUserById(additional.UserId) is not null)
            {
                yield return additional.UserId;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var item = e.Item;
        if (e.Session is null || item is null)
        {
            return;
        }

        // A published channel item that ran to the end: the viewer is on a channel in an
        // app the injected script never reaches, so the schedule only keeps running if the
        // server hands them the next program. Playing the same item again is all it takes -
        // its media resolves to whatever is on air by then.
        if (_liveOffset.ChannelIdFor(item) is { } publishedChannelId)
        {
            if (e.PlayedToCompletion)
            {
                _ = ContinueChannelAsync(e.Session.Id, item, publishedChannelId);
            }

            return;
        }

        if (!_tuned.TryGetValue(e.Session.Id, out var tuned))
        {
            return;
        }

        tuned.Touch();

        // Deliberately nothing is released here. A shield used to be dropped when the item
        // it covered stopped playing, which looks tidy and is wrong twice over:
        //
        // - A stop followed by a start of the same item - a seek, a re-prepare after the
        //   library scan touched the file, a client rebuilding its player - decremented the
        //   arm belonging to the playback that had just begun, not the one that ended. The
        //   settle delay was meant to catch that and cannot: whether the new playback has
        //   reported itself yet is a race.
        // - Anything the client starts without telling us is then unshielded, and a player
        //   that re-prepares on its own does exactly that.
        //
        // So a tuned session holds what it is airing until the viewer leaves it, which is
        // the moment that actually means something. Untune, the session ending and the
        // hours-long prune all release in full, so nothing is held indefinitely.
        if (tuned.HasLeft)
        {
            // This is the stop the session was being kept alive for: the viewer left the
            // channel and their player has now torn down. Nothing follows it.
            if (_tuned.TryGetValue(e.Session.Id, out var current) && ReferenceEquals(current, tuned))
            {
                _tuned.TryRemove(new KeyValuePair<string, TunedSession>(e.Session.Id, tuned));
            }

            return;
        }

        if (!tuned.FollowSchedule)
        {
            // Script-driven session: it decides what plays next and untunes itself when the
            // viewer leaves. Note that this stop arrives *after* the script has registered
            // the follow-up, so there is nothing to conclude about the session from it.
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
            // Nothing pushes a follow-up here, so the viewer either stopped the channel or
            // seeked - and a seek must not end the channel, so the session is only let go
            // once it has really gone quiet.
            _ = UntuneUnlessResumedAsync(e.Session.Id);
        }
    }

    private async Task UntuneUnlessResumedAsync(string sessionId)
    {
        await Task.Delay(StopSettleDelay).ConfigureAwait(false);
        if (!StillPlaying(sessionId))
        {
            Untune(sessionId);
        }
    }

    /// <summary>
    /// How long to wait before believing a stop. Seeking is reported as one: the client tears
    /// the running stream down and starts a new one at the new position, and the stop that
    /// goes out in between is indistinguishable from the programme ending - a seek into the
    /// last minutes even reports it as played to completion. What tells the two apart is what
    /// the session is doing a moment later, and this is how long that moment is.
    /// </summary>
    private static readonly TimeSpan StopSettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets a value indicating whether a session is still playing after a stop, which means
    /// the stop was a stream being swapped out rather than playback ending.
    /// </summary>
    /// <param name="sessionId">The session the stop came from.</param>
    /// <param name="itemId">The item that stopped, or null for any item.</param>
    private bool StillPlaying(string sessionId, Guid? itemId = null)
    {
        var playing = FindSession(sessionId)?.NowPlayingItem;
        return playing is not null && (itemId is null || playing.Id == itemId.Value);
    }

    /// <summary>
    /// Keeps a published channel running on a client the injected script cannot reach: the
    /// program that just finished is followed by sending the channel item again, positioned
    /// where the schedule now stands.
    /// </summary>
    private async Task ContinueChannelAsync(string sessionId, BaseItem finished, Guid channelId)
    {
        try
        {
            // Let the client finish tearing the finished program down first.
            await Task.Delay(StopSettleDelay).ConfigureAwait(false);

            if (StillPlaying(sessionId))
            {
                // The viewer seeked: the stop was the old stream going away, and the new one
                // is already running. Sending the channel now would stop the programme they
                // just skipped through and drop them back at the live position.
                return;
            }

            var at = DateTime.UtcNow;
            var now = _liveOffset.Resolve(channelId, at);
            if (now?.Entry is null)
            {
                return; // the channel has nothing to air at the moment
            }

            var next = await FindEntryAsync(finished, channelId, now.Entry.ItemId).ConfigureAwait(false);
            if (next is null)
            {
                _logger.LogWarning("LiteTV: could not find the follow-up entry for {Channel} to continue with.", channelId);
                return;
            }

            await _sessionManager.SendPlayCommand(
                sessionId,
                sessionId,
                new PlayRequest
                {
                    ItemIds = new[] { next.Id },
                    StartPositionTicks = now.OffsetAt(at),
                    PlayCommand = PlayCommand.PlayNow
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not continue the channel on session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Gets the entry standing for one of a channel's programs, asking the channel manager
    /// for the folder's contents so the entry is built and stored first - until that happens
    /// it does not exist as something playable.
    /// </summary>
    private async Task<BaseItem?> FindEntryAsync(BaseItem sibling, Guid channelId, Guid programId)
    {
        var wanted = LiteTvChannelProvider.NowPlayingId(channelId, programId);

        // The channel has to be named as well as the folder: asking for the folder alone
        // makes the channel manager index an empty channel list and throw.
        var query = new InternalItemsQuery
        {
            ParentId = sibling.ParentId,
            ChannelIds = new[] { sibling.ChannelId }
        };

        var result = await _channelManager
            .GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None)
            .ConfigureAwait(false);

        return result.Items.FirstOrDefault(i => string.Equals(i.ExternalId, wanted, StringComparison.Ordinal));
    }

    private async Task PushCurrentAsync(string sessionId, Guid channelId)
    {
        try
        {
            // Give the client a moment to settle after the stop report.
            await Task.Delay(StopSettleDelay).ConfigureAwait(false);

            if (StillPlaying(sessionId))
            {
                return; // a seek, not the end of the programme
            }

            var at = DateTime.UtcNow;
            var now = _liveOffset.Resolve(channelId, at);
            if (now?.Entry is null)
            {
                return;
            }

            // Shield before the command goes out: the client can report playback the
            // moment it receives it. Nothing is pushed if that fails - a channel that stops
            // is a nuisance, a channel that writes itself into the watch history is not.
            if (!Tune(sessionId, channelId, followSchedule: true, now.Entry.ItemId))
            {
                Untune(sessionId);
                return;
            }

            await _sessionManager.SendPlayCommand(
                sessionId,
                sessionId,
                new PlayRequest
                {
                    ItemIds = new[] { now.Entry.ItemId },
                    StartPositionTicks = now.OffsetAt(at),
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
            if (pair.Value.LastActivityUtc < cutoff && _tuned.TryRemove(pair.Key, out _))
            {
                // Nothing has played in this session for hours, so there is no stop left to
                // wait for: release straight away rather than going through Untune.
                _shield.ReleaseSession(pair.Key, TimeSpan.Zero);
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

        /// <summary>
        /// Gets a value indicating whether the viewer has left and the session is only still
        /// around to catch the stop of the program that was on air.
        /// </summary>
        public bool HasLeft { get; private set; }

        public void Touch() => LastActivityUtc = DateTime.UtcNow;

        public void MarkLeft() => HasLeft = true;

        public void MarkTuned() => HasLeft = false;
    }
}
