using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using Jellyfin.Plugin.LiteTv.Trailers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiteTv;

/// <summary>
/// Registers the plugin's services with the server's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChannelPlaylistBuilder>();

        // The stored weeks: one file per channel, and the schedule itself rather than a cache
        // of one. Singleton so the files are read once and held.
        serviceCollection.AddSingleton<WeekStore>();

        // The single answer to "what is on this channel", shared by the guide endpoints and
        // anything else that needs to know.
        serviceCollection.AddSingleton<ChannelGuide>();

        // Hands clients the account channel playback runs under, which is the whole of how
        // channel viewing is kept off the account people watch under.
        serviceCollection.AddSingleton<ChannelPlaybackUser>();

        // Turns the YouTube links a library holds instead of trailer files into streams a
        // player can be handed. Singleton so the resolved URLs are cached across requests.
        serviceCollection.AddSingleton<YouTubeStreamResolver>();

        // Which parts of a trailer are not the trailer. Singleton for the same reason: the
        // segments of a trailer a channel airs every few hours should be fetched once.
        serviceCollection.AddSingleton<SponsorBlockClient>();
    }
}
