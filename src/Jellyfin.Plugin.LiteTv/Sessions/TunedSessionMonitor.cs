using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiteTv.Core;
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
/// of its watch state is ever written. The shield is released when the item stops and when
/// the viewer leaves the channel.
/// Only registered items are covered: an item the viewer starts themselves in a session
/// that is still marked tuned records its watch state as usual.
/// For sessions tuned via the PlayOn endpoint (native clients without the injected script)
/// this also pushes the next scheduled item when an item plays to the end.
/// </summary>
public class TunedSessionMonitor : IHostedService
{
    private static readonly TimeSpan TunedSessionLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// How long an item stays shielded after it stopped. The client's final playback report
    /// travels independently of the stop - it is sent while the player is torn down, after
    /// the transcode is stopped - and regularly lands well after it.
    /// </summary>
    private static readonly TimeSpan ReleaseGracePeriod = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchStateShield _shield;
    private readonly ChannelPlaylistBuilder _playlistBuilder;
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
    /// <param name="playlistBuilder">The channel playlist builder.</param>
    /// <param name="logger">The logger.</param>
    public TunedSessionMonitor(
        ISessionManager sessionManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchStateShield shield,
        ChannelPlaylistBuilder playlistBuilder,
        ILogger<TunedSessionMonitor> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _shield = shield;
        _playlistBuilder = playlistBuilder;
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
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
    public void Tune(string sessionId, Guid channelId, bool followSchedule, Guid? itemId = null)
    {
        // Preserve an existing session across the repeated calls the script makes; only
        // the first call establishes followSchedule.
        var tuned = _tuned.GetOrAdd(sessionId, _ => new TunedSession(channelId, followSchedule));
        tuned.Touch();

        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            foreach (var user in GetSessionUsers(sessionId))
            {
                _shield.Arm(sessionId, user.Id, itemId.Value);
            }
        }

        Prune();
    }

    /// <summary>
    /// Removes the tuned mark from a session and releases everything it still shields.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    public void Untune(string sessionId)
    {
        _tuned.TryRemove(sessionId, out _);
        _shield.ReleaseSession(sessionId, ReleaseGracePeriod);
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
            _shield.Release(e.Session.Id, user.Id, item.Id, ReleaseGracePeriod);
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
            // Nothing pushes a follow-up here, so the viewer stopped the channel.
            Untune(e.Session.Id);
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

            // Shield before the command goes out: the client can report playback the
            // moment it receives it.
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
            if (pair.Value.LastActivityUtc < cutoff)
            {
                Untune(pair.Key);
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

        public void Touch() => LastActivityUtc = DateTime.UtcNow;
    }
}
