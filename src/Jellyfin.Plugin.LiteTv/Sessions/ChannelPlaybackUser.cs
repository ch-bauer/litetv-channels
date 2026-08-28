using System.Security.Cryptography;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Hands out the credentials a client plays a channel with: a Jellyfin account of the
/// plugin's own, kept apart from the accounts people actually watch under.
/// <para>
/// This is what keeps channel viewing off the account, and it replaces the watch-state
/// shield that used to do the job. The shield tried to suppress writes while a channel
/// was on; it could never be made correct, because a stop is not the end of anything and
/// a player that re-prepares itself - after a library scan touches the file underneath
/// it, say - reports playback nobody asked for. There was always a window to be outside
/// of.
/// </para>
/// <para>
/// The server resolves the user a playback report belongs to from <em>the token on the
/// request</em>, not from the session and not from the item. Measured on 10.11.11: a full
/// playback reported with this user's token moved that user's play count and left every
/// one of the real account's items untouched. There is no window here to be outside of -
/// the writes land somewhere harmless because of who asked, not because of when.
/// </para>
/// <para>
/// The account is deliberately dull: hidden from the login screen, no administration, no
/// deleting, no downloading. It can still see the libraries, because it has to play from
/// them. Note the consequence: anyone who can call this endpoint can obtain a token for
/// it, so on a server with several people and restricted libraries this account should be
/// restricted to match - see the configuration page.
/// </para>
/// </summary>
public sealed class ChannelPlaybackUser
{
    /// <summary>
    /// Identifies the plugin's own sessions to the server. Constant on purpose: every
    /// client asking for these credentials shares one device, so the dashboard shows a
    /// single "LiteTV" session rather than one per television.
    /// </summary>
    /// <remarks>
    /// The cost of one shared device id is why <c>ChannelUserToken</c> exists:
    /// <b>Jellyfin keeps one session per device id</b>, so authenticating again revokes the
    /// token the last caller is playing with. Every authentication here is somebody else's
    /// stream ending, and the code below authenticates as rarely as it possibly can.
    /// </remarks>
    private const string DeviceId = "litetv-channel-playback";

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IDeviceManager _deviceManager;
    private readonly ILogger<ChannelPlaybackUser> _logger;

    /// <summary>
    /// Serialises the whole get-or-create-then-authenticate dance. Two clients tuning in
    /// at once would otherwise race to create the same account, and the loser's
    /// <see cref="IUserManager.CreateUserAsync"/> throws.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ChannelCredentials? _cached;

    /// <summary>
    /// Gets the token last minted or adopted, held in memory as well as saved.
    /// </summary>
    /// <remarks>
    /// This exists for the same reason <c>ProofOfOrigin.Held</c> does, and against the same
    /// trap: <b>a configuration save carries the whole configuration</b>, so any writer that
    /// does not know about this field sends it back empty and wipes it. For this field that
    /// would be worse than losing a setting - the next tune-in would authenticate afresh and
    /// stop whatever is playing, so pressing Save would end a stream. <c>Plugin</c> fills the
    /// gap back in from here. Cleared by <see cref="Forget"/>, so a save that is MEANT to
    /// clear the token still can.
    /// </remarks>
    internal static string HeldToken { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelPlaybackUser"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="deviceManager">The device manager, used to check a stored token is alive.</param>
    /// <param name="logger">The logger.</param>
    public ChannelPlaybackUser(
        IUserManager userManager,
        ISessionManager sessionManager,
        IDeviceManager deviceManager,
        ILogger<ChannelPlaybackUser> logger)
    {
        _userManager = userManager;
        _sessionManager = sessionManager;
        _deviceManager = deviceManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the credentials to play a channel with, creating the account on first use.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The credentials, or null when the account could not be prepared.</returns>
    public async Task<ChannelCredentials?> GetAsync(CancellationToken cancellationToken)
    {
        // A token stays good until it is revoked, so the usual case answers without
        // touching the database at all.
        if (_cached is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } wonTheRace)
            {
                return wonTheRace;
            }

            var credentials = await PrepareAsync().ConfigureAwait(false);
            _cached = credentials;
            return credentials;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets the token, in memory and on disk, so the next caller authenticates afresh.
    /// Used when the configured account name changes: the old token belongs to the wrong
    /// account, and leaving it stored would have the plugin hand out credentials for an
    /// account nobody asked for.
    /// </summary>
    public void Forget()
    {
        _cached = null;

        var config = Plugin.Instance?.Configuration;
        if (config is null || config.ChannelUserToken.Length == 0)
        {
            return;
        }

        // Cleared here FIRST, or the gap-filling in Plugin.UpdateConfiguration would put
        // it straight back and Forget would be a no-op.
        HeldToken = string.Empty;
        config.ChannelUserToken = string.Empty;
        Plugin.Instance!.UpdateConfiguration(config);
    }

    /// <summary>
    /// Whether a token is one the server will still accept, and belongs to this account.
    /// </summary>
    /// <remarks>
    /// A pure read: it asks which device holds this token and touches nothing. Deliberately
    /// not <c>GetSessionByAuthenticationToken</c>, which answers the same question but
    /// brings a session into being as a side effect of the asking.
    /// </remarks>
    internal bool IsAlive(string token, Guid userId)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            var devices = _deviceManager.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 });

            // The token has to belong to the account it is about to be handed out for. A
            // renamed account can leave a live token behind that plays as the wrong user,
            // and playing as the wrong user is the one thing this class exists to prevent.
            var device = devices.Items.Count > 0 ? devices.Items[0] : null;
            return device is not null && device.UserId.Equals(userId);
        }
        catch (Exception ex)
        {
            // Never fatal: not knowing whether a token is alive is answered by minting a
            // new one, which costs a stream but not the feature.
            _logger.LogWarning(ex, "LiteTV: could not check the stored playback token; assuming it is dead");
            return false;
        }
    }

    private async Task<ChannelCredentials?> PrepareAsync()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(config.ChannelUserName)
            ? PluginConfigurationDefaults.ChannelUserName
            : config.ChannelUserName.Trim();

        try
        {
            var user = _userManager.GetUserByName(name);
            if (user is null)
            {
                user = await _userManager.CreateUserAsync(name).ConfigureAwait(false);
                _logger.LogInformation("LiteTV: created the channel playback account {Name}", name);
            }

            // The password is the plugin's own and is never typed by anyone, so it is
            // generated rather than chosen, and rewritten whenever it is missing - which
            // is also how an account someone created by hand is adopted.
            if (string.IsNullOrEmpty(config.ChannelUserPassword))
            {
                config.ChannelUserPassword = NewPassword();
                Plugin.Instance!.UpdateConfiguration(config);
            }

            await RestrictAsync(user.Id).ConfigureAwait(false);

            /*
                The stored token first, and this is the whole point of the exercise.

                Authenticating revokes whatever token this device already holds, so a restart,
                a plugin install or a second television tuning in used to end a stream that
                was playing - on a television, a video that loads for ever, because the
                stream's own requests are being refused. Reusing a token that is still good
                means none of those do.
            */
            if (IsAlive(config.ChannelUserToken, user.Id))
            {
                HeldToken = config.ChannelUserToken;

                _logger.LogInformation(
                    "LiteTV: reusing the stored playback token for {Name}; nothing playing is interrupted",
                    name);

                return new ChannelCredentials
                {
                    UserId = user.Id,
                    UserName = name,
                    AccessToken = config.ChannelUserToken
                };
            }

            /*
                Only now is the password written.

                It used to be rewritten on every single call. Changing an account's password
                is itself grounds for the server to revoke its tokens, so the path that was
                meant to be cheap was quietly making the expensive one necessary.
            */
            await _userManager.ChangePassword(user.Id, config.ChannelUserPassword).ConfigureAwait(false);

            _logger.LogInformation(
                "LiteTV: authenticating {Name} afresh; any stream already playing on this account will stop",
                name);

            var result = await _sessionManager.AuthenticateNewSession(new AuthenticationRequest
            {
                Username = name,
                Password = config.ChannelUserPassword,
                App = "LiteTV",
                AppVersion = typeof(ChannelPlaybackUser).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                DeviceId = DeviceId,
                DeviceName = "LiteTV Channels"
            }).ConfigureAwait(false);

            if (string.IsNullOrEmpty(result.AccessToken))
            {
                _logger.LogError("LiteTV: the channel playback account authenticated without a token");
                return null;
            }

            // Written down, so the next restart reuses this token instead of revoking it.
            HeldToken = result.AccessToken;
            config.ChannelUserToken = result.AccessToken;
            Plugin.Instance!.UpdateConfiguration(config);

            return new ChannelCredentials
            {
                UserId = user.Id,
                UserName = name,
                AccessToken = result.AccessToken
            };
        }
        catch (Exception ex)
        {
            // Losing this is not fatal to the server, but it is fatal to the point of the
            // plugin: without it a channel would play and be recorded like ordinary
            // watching. Clients are told, and refuse to start.
            _logger.LogError(ex, "LiteTV: could not prepare the channel playback account {Name}", name);
            return null;
        }
    }

    /// <summary>
    /// Takes away everything the account does not need. It exists to play files and to
    /// collect the watch state nobody wants, and should be able to do nothing else.
    /// </summary>
    private async Task RestrictAsync(Guid userId)
    {
        var policy = _userManager.GetUserDto(_userManager.GetUserById(userId)!).Policy;
        if (policy is null)
        {
            return;
        }

        policy.IsAdministrator = false;
        policy.IsHidden = true;
        policy.IsDisabled = false;
        policy.EnableContentDeletion = false;
        policy.EnableContentDownloading = false;
        policy.EnableUserPreferenceAccess = false;
        policy.EnableRemoteControlOfOtherUsers = false;
        policy.EnableSharedDeviceControl = false;
        policy.EnableCollectionManagement = false;
        policy.EnableSubtitleManagement = false;
        policy.EnableLiveTvAccess = false;
        policy.EnableLiveTvManagement = false;

        // No transcoding, ever. That is the plugin's founding claim - a channel plays the
        // library's own files, straight - and enforcing it on the account is what makes it
        // true rather than merely intended. A client that cannot direct play a programme now
        // fails loudly instead of quietly putting the server to work on a transcode nobody
        // asked for and nobody would see was happening.
        policy.EnableVideoPlaybackTranscoding = false;

        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    }

    private static string NewPassword() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}

/// <summary>
/// The credentials a client plays a channel with.
/// </summary>
public sealed class ChannelCredentials
{
    /// <summary>Gets or sets the account's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the account's name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the access token to send playback requests with.</summary>
    public string AccessToken { get; set; } = string.Empty;
}
