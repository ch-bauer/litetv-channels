using Jellyfin.Plugin.LiteTv.Channels;
using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // The single answer to "what is on this channel", shared by the web guide, the
        // published channel items, the Live TV service and the session monitor.
        serviceCollection.AddSingleton<ChannelGuide>();

        serviceCollection.AddSingleton<WatchStateShield>();
        // The shield needs to know which client a watch-state write came from; registering
        // the accessor is a no-op when the server already did.
        serviceCollection.AddHttpContextAccessor();
        ShieldUserData(serviceCollection);

        // Tracks tuned sessions: shields what the channel plays for all of them, and
        // schedule-following pushes for sessions tuned via the PlayOn endpoint.
        serviceCollection.AddSingleton<TunedSessionMonitor>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TunedSessionMonitor>());

        // Publishes the channels to every client, not just the injected web UI.
        serviceCollection.AddSingleton<IChannel, LiteTvChannelProvider>();

        serviceCollection.AddSingleton<LiveOffsetResolver>();

        // Keeps the channel out of My Media when the setting asks for it, on every client.
        serviceCollection.AddHostedService<MyMediaVisibility>();
    }

    /// <summary>
    /// Puts <see cref="ShieldedUserDataManager"/> in front of the server's user data
    /// manager, so watch state for an item a channel is playing is never written in the
    /// first place. Everything that records watch state goes through that service, which
    /// makes this the one interception point that covers every client, whether playback
    /// was started by the injected script, by a native app or by a cast command.
    /// It only works because the server registers its own services before it hands the
    /// collection to the plugins, so the registration replaced here is the real one. If
    /// that ever stops being true the existing registration is not found, nothing is
    /// replaced, and <see cref="TunedSessionMonitor"/> reports it at startup.
    /// </summary>
    private static void ShieldUserData(IServiceCollection serviceCollection)
    {
        var registered = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IUserDataManager));
        if (registered is null)
        {
            return;
        }

        Func<IServiceProvider, IUserDataManager>? resolveInner = null;
        if (registered.ImplementationType is { } implementationType)
        {
            // Register the server's implementation under its own type so it is still built
            // (and built only once) by the container, then wrap that instance.
            serviceCollection.AddSingleton(implementationType);
            resolveInner = sp => (IUserDataManager)sp.GetRequiredService(implementationType);
        }
        else if (registered.ImplementationInstance is IUserDataManager instance)
        {
            resolveInner = _ => instance;
        }
        else if (registered.ImplementationFactory is { } factory)
        {
            resolveInner = sp => (IUserDataManager)factory(sp);
        }

        if (resolveInner is null)
        {
            return;
        }

        serviceCollection.Remove(registered);
        serviceCollection.AddSingleton<IUserDataManager>(sp => new ShieldedUserDataManager(
            resolveInner(sp),
            sp.GetRequiredService<WatchStateShield>(),
            sp.GetRequiredService<LiveOffsetResolver>(),
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetRequiredService<ILogger<ShieldedUserDataManager>>()));
    }
}
