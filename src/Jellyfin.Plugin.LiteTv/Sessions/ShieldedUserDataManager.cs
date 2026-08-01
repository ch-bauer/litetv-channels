using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Sessions;

/// <summary>
/// Wraps the server's own user data manager and drops writes for items a LiteTV channel is
/// currently playing.
/// Every route that records watch state - playback start, progress reports, stop, mark
/// played - ends up in <see cref="IUserDataManager.SaveUserData(User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken)"/>,
/// so this is the one place where channel viewing can be kept off the account for good,
/// no matter which client played it or how playback was started. Everything else is passed
/// straight through.
/// </summary>
internal sealed class ShieldedUserDataManager : IUserDataManager
{
    private readonly IUserDataManager _inner;
    private readonly WatchStateShield _shield;
    private readonly ILogger<ShieldedUserDataManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShieldedUserDataManager"/> class.
    /// </summary>
    /// <param name="inner">The server's user data manager.</param>
    /// <param name="shield">The set of items currently airing on a channel.</param>
    /// <param name="logger">The logger.</param>
    public ShieldedUserDataManager(IUserDataManager inner, WatchStateShield shield, ILogger<ShieldedUserDataManager> logger)
    {
        _inner = inner;
        _shield = shield;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<UserDataSaveEventArgs> UserDataSaved
    {
        add => _inner.UserDataSaved += value;
        remove => _inner.UserDataSaved -= value;
    }

    /// <inheritdoc />
    public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
    {
        if (_shield.IsShielded(user.Id, item.Id))
        {
            _logger.LogDebug("LiteTV: dropped a {Reason} write for {Item}, it is airing on a channel.", reason, item.Name);
            return;
        }

        _inner.SaveUserData(user, item, userData, reason, cancellationToken);
    }

    /// <inheritdoc />
    public void SaveUserData(User user, BaseItem item, UpdateUserItemDataDto userDataDto, UserDataSaveReason reason)
    {
        // Not shielded: this overload is the deliberate "set the watch state of this item"
        // API behind the client's own mark-watched and set-position actions, which the
        // viewer means even while a channel is running.
        _inner.SaveUserData(user, item, userDataDto, reason);
    }

    /// <inheritdoc />
    public UserItemData GetUserData(User user, BaseItem item) => _inner.GetUserData(user, item);

    /// <inheritdoc />
    public UserItemDataDto GetUserDataDto(BaseItem item, User user) => _inner.GetUserDataDto(item, user);

    /// <inheritdoc />
    public UserItemDataDto GetUserDataDto(BaseItem item, BaseItemDto? itemDto, User user, DtoOptions options)
        => _inner.GetUserDataDto(item, itemDto, user, options);

    /// <inheritdoc />
    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
        => _inner.UpdatePlayState(item, data, reportedPositionTicks);
}
