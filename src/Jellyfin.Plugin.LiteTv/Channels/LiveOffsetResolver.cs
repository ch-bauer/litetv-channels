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

        var now = Resolve(channelId.Value);
        if (now is null)
        {
            return null;
        }

        // Only while the entry still names what is on air. A client holding an older
        // listing would otherwise be told to resume a finished program at the position the
        // channel has since moved on to.
        var programId = LiteTvChannelProvider.ProgramIdFromItemId(item.ExternalId!);
        return programId == now.Current.ItemId ? now.OffsetTicks : null;
    }

    /// <summary>
    /// Gets what a channel is airing right now and how far into it.
    /// </summary>
    /// <param name="channelId">The LiteTV channel id.</param>
    /// <returns>The resolved position, or null when the channel has nothing to air.</returns>
    public ScheduleNow? Resolve(Guid channelId)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        var builder = _serviceProvider.GetService<ChannelPlaylistBuilder>();
        if (channel is null || builder is null)
        {
            return null;
        }

        return ScheduleResolver.Resolve(builder.GetEntries(channel), channel.AnchorUtc, DateTime.UtcNow);
    }

    /// <summary>
    /// Gets the program a channel plays after the given one. Read off the channel's own
    /// order rather than the clock, so it stays right for a viewer who is ahead of the
    /// schedule after skipping through a program.
    /// </summary>
    /// <param name="channelId">The LiteTV channel id.</param>
    /// <param name="programId">The program currently playing.</param>
    /// <returns>The next program, or null when the channel has only this one.</returns>
    public Guid? ProgramAfter(Guid channelId, Guid programId)
    {
        var channel = Plugin.Instance?.Configuration.Channels
            .FirstOrDefault(c => c.Id == channelId && c.Enabled);
        var builder = _serviceProvider.GetService<ChannelPlaylistBuilder>();
        if (channel is null || builder is null)
        {
            return null;
        }

        var entries = builder.GetEntries(channel).Where(e => e.RuntimeTicks > 0).ToList();
        if (entries.Count < 2)
        {
            return null;
        }

        var index = entries.FindIndex(e => e.ItemId == programId);
        return index < 0 ? null : entries[(index + 1) % entries.Count].ItemId;
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
