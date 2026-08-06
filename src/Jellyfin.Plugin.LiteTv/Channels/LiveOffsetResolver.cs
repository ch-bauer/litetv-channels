using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Works out how far into its current program a published channel is, so the item can be
/// reported to clients as resumable at exactly that point. That is what makes pressing play
/// on a TV app join the channel live rather than start the program from the top: a channel
/// item's media carries no start position, but a resume position is honoured by every client.
/// </summary>
public sealed class LiveOffsetResolver
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveOffsetResolver"/> class.
    /// </summary>
    /// <param name="serviceProvider">Used to reach the playlist builder only when a channel
    /// item is actually being described. Resolving it up front would close a dependency
    /// circle, since this is reached from the user data manager the library itself uses.</param>
    public LiveOffsetResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets how far into the current program the channel behind this item is.
    /// </summary>
    /// <param name="item">Any library item; only this plugin's channel items resolve.</param>
    /// <returns>The offset in ticks, or null when the item is not one of ours.</returns>
    public long? OffsetTicksFor(BaseItem item)
    {
        var channelId = ChannelIdFor(item);
        if (channelId is null)
        {
            return null;
        }

        var at = DateTime.UtcNow;
        var airing = Resolve(channelId.Value, at);
        if (airing?.Entry is null)
        {
            return null;
        }

        // EXPERIMENT: the id no longer names a program, so there is nothing to go stale and
        // nothing to compare. The entry always stands for whatever the channel is airing now,
        // which is what a channel is, and the offset is always the live one.
        return airing.OffsetAt(at);
    }

    /// <summary>
    /// Gets what a channel is airing right now and how far into it.
    /// </summary>
    /// <param name="channelId">The LiteTV channel id.</param>
    /// <param name="utc">The moment to resolve at; defaults to now.</param>
    /// <returns>The airing, or null when the channel has nothing to air.</returns>
    public Airing? Resolve(Guid channelId, DateTime? utc = null)
    {
        var channel = ChannelGuide.Channel(channelId);
        var guide = _serviceProvider.GetService<ChannelGuide>();
        if (channel is null || guide is null)
        {
            return null;
        }

        return guide.NowOn(channel, utc);
    }

    /// <summary>
    /// Gets the LiteTV channel an item belongs to, when it is one of the published channel
    /// items. Cheap: it reads the item's own identifiers and touches no schedule.
    /// </summary>
    /// <param name="item">Any library item.</param>
    /// <returns>The channel id, or null when the item is not one of ours.</returns>
    public Guid? ChannelIdFor(BaseItem item)
    {
        if (item.ChannelId.Equals(Guid.Empty) || string.IsNullOrEmpty(item.ExternalId))
        {
            return null;
        }

        return LiteTvChannelProvider.ChannelIdFromItemId(item.ExternalId);
    }
}
