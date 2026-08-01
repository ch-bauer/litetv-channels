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
        if (item.ChannelId.Equals(Guid.Empty) || string.IsNullOrEmpty(item.ExternalId))
        {
            return null;
        }

        var channelId = LiteTvChannelProvider.ChannelIdFromItemId(item.ExternalId);
        if (channelId is null)
        {
            return null;
        }

        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId.Value && c.Enabled);
        if (channel is null)
        {
            return null;
        }

        var builder = _serviceProvider.GetService<ChannelPlaylistBuilder>();
        if (builder is null)
        {
            return null;
        }

        var now = ScheduleResolver.Resolve(builder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
        return now?.OffsetTicks;
    }
}
