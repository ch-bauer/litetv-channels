using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiteTv.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
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
    private static readonly PropertyInfo[] CopyableProperties = typeof(UserItemData)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    private readonly IUserDataManager _inner;
    private readonly WatchStateShield _shield;
    private readonly LiveOffsetResolver _liveOffset;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ShieldedUserDataManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShieldedUserDataManager"/> class.
    /// </summary>
    /// <param name="inner">The server's user data manager.</param>
    /// <param name="shield">The set of items currently airing on a channel.</param>
    /// <param name="liveOffset">Resolves how far into its program a published channel is.</param>
    /// <param name="httpContextAccessor">Accessor for the request a save belongs to, used to
    /// tell the channel's own playback apart from the same title watched elsewhere.</param>
    /// <param name="logger">The logger.</param>
    public ShieldedUserDataManager(
        IUserDataManager inner,
        WatchStateShield shield,
        LiveOffsetResolver liveOffset,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ShieldedUserDataManager> logger)
    {
        _inner = inner;
        _shield = shield;
        _liveOffset = liveOffset;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
    {
        add => _inner.UserDataSaved += value;
        remove => _inner.UserDataSaved -= value;
    }

    /// <inheritdoc />
    public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
    {
        if (MustNotRecord(user.Id, item))
        {
            _logger.LogDebug("LiteTV: dropped a {Reason} write for {Item}, it belongs to a channel.", reason, item.Name);
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
    public UserItemData? GetUserData(User user, BaseItem item)
    {
        var data = _inner.GetUserData(user, item);
        if (data is null || !MustNotRecord(user.Id, item))
        {
            return data;
        }

        // Hand out a copy. The server keeps one cached UserItemData instance per user and
        // item and returns that very instance here, while everything that records watch
        // state works the same way: read it, change it, then ask for it to be saved.
        // Dropping only the save would leave the item showing as played anyway, because
        // the cached instance - the one every other read goes through - was already
        // changed. Against a copy those changes land nowhere.
        return Copy(data);
    }

    /// <inheritdoc />
    public UserItemDataDto? GetUserDataDto(BaseItem item, User user)
        => JoinLive(item, _inner.GetUserDataDto(item, user));

    /// <inheritdoc />
    public UserItemDataDto? GetUserDataDto(BaseItem item, BaseItemDto? itemDto, User user, DtoOptions options)
        => JoinLive(item, _inner.GetUserDataDto(item, itemDto, user, options));

    /// <summary>
    /// Reports a published channel as resumable at the point its current program has reached.
    /// A channel item's media cannot carry a start position, but every client honours a
    /// resume position, so this is what lets a TV app join the channel live instead of
    /// starting the program from the top.
    /// </summary>
    private UserItemDataDto? JoinLive(BaseItem item, UserItemDataDto? data)
    {
        if (data is null)
        {
            return data;
        }

        var offsetTicks = _liveOffset.OffsetTicksFor(item);
        if (offsetTicks is null or <= 0)
        {
            return data;
        }

        data.PlaybackPositionTicks = offsetTicks.Value;
        var runtime = item.RunTimeTicks ?? 0;
        if (runtime > 0)
        {
            data.PlayedPercentage = Math.Clamp(offsetTicks.Value * 100d / runtime, 0d, 100d);
        }

        return data;
    }

    /// <inheritdoc />
    public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks)
        => _inner.UpdatePlayState(item, data, reportedPositionTicks);

    /// <summary>
    /// Whether the item's watch state must not be recorded at all: either a program a
    /// channel is airing, or one of the published channel items themselves.
    /// A channel item is never recorded because it is not a thing anyone watches to the
    /// end - it is the channel. Left to itself it would collect a resume position from
    /// every viewing and sit in Continue Watching for good, and the position it is
    /// reported with is the live one anyway, which is computed rather than remembered.
    /// </summary>
    private bool MustNotRecord(Guid userId, BaseItem item)
    {
        return _liveOffset.ChannelIdFor(item) is not null || IsChannelPlayback(userId, item.Id);
    }

    /// <summary>
    /// Decides whether this write belongs to a channel playing the item, as opposed to the
    /// same title being watched deliberately somewhere else while it happens to be on air -
    /// which has to keep its watch state like any other viewing.
    /// The two are told apart by the device the request came from: a save carries no session
    /// of its own, but the client that reported it identifies its device in the
    /// authorization header. A request that cannot be attributed to a device is treated as
    /// the channel's, so an unrecognised caller cannot write channel viewing to the account.
    /// </summary>
    private bool IsChannelPlayback(Guid userId, Guid itemId)
    {
        // Checked first: for everything not currently airing this is a single dictionary
        // miss, and that is the overwhelming majority of the server's user data writes.
        if (!_shield.TryGetShieldedDevices(userId, itemId, out var deviceIds))
        {
            return false;
        }

        if (deviceIds.Count == 0)
        {
            return true; // shielded everywhere: the airing device is not known
        }

        var requestDevice = RequestDeviceId();
        return requestDevice is null || deviceIds.Contains(requestDevice, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the device id out of the request's authorization header, the same field the
    /// server itself identifies a client's session by.
    /// </summary>
    private string? RequestDeviceId()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request is null)
        {
            return null;
        }

        foreach (var header in AuthorizationHeader.Names)
        {
            if (request.Headers.TryGetValue(header, out var values)
                && AuthorizationHeader.DeviceId(values.ToString()) is { } deviceId)
            {
                return deviceId;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies every value across by reflection rather than field by field, so a property
    /// added to <see cref="UserItemData"/> in a later server release is carried over
    /// instead of silently reading back as its default.
    /// </summary>
    private static UserItemData Copy(UserItemData source)
    {
        var copy = new UserItemData { Key = source.Key };
        foreach (var property in CopyableProperties)
        {
            property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }
}
