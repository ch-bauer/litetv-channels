using Jellyfin.Plugin.LiteTv.Channels;
using Jellyfin.Plugin.LiteTv.Core;
using Jellyfin.Plugin.LiteTv.Sessions;
using Jellyfin.Plugin.LiteTv.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
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

        // And into the server's own Live TV section, so its guide - the one native clients
        // already have - shows what the channels are airing. Registration is unconditional
        // because it happens once at startup; the service itself reports no channels while
        // the option is off, so the setting can be changed without a restart.
        serviceCollection.AddSingleton<ILiveTvService, LiteTvLiveService>();
        serviceCollection.AddSingleton<LiveOffsetResolver>();

        // Programme artwork only reaches a client once the image metadata the guide refresh
        // leaves blank has been filled in. The repair runs at startup as a hosted service and
        // again after every refresh, which is what the guide manager is wrapped for.
        serviceCollection.AddSingleton<ProgramImageRepair>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ProgramImageRepair>());
        RepairProgramImages(serviceCollection);

        // Preferred: register the script injection with the File Transformation
        // plugin when installed (same mechanism as Intro Skipper).
        serviceCollection.AddHostedService<FileTransformationRegistration>();

        // Fallback: request-time injection into the web client's index.html; works
        // even when the web directory on disk is read-only. Stands down when File
        // Transformation handles the injection.
        serviceCollection.AddSingleton<IStartupFilter, InjectionStartupFilter>();
    }

    /// <summary>
    /// Puts <see cref="RepairingGuideManager"/> in front of the server's guide manager, so a
    /// finished guide refresh is followed by the image metadata repair. The same reasoning as
    /// <see cref="ShieldUserData"/> applies: the server registers its own services first, so
    /// the registration replaced here is the real one, and if that ever stops being true
    /// nothing is found and nothing is wrapped.
    /// <para>
    /// A refresh raises no event that a plugin could subscribe to instead - the server
    /// suppresses item notifications for Live TV on purpose - so wrapping the call is what
    /// makes the repair run at the only moment it needs to.
    /// </para>
    /// </summary>
    private static void RepairProgramImages(IServiceCollection serviceCollection)
    {
        var registered = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IGuideManager));
        if (registered is null)
        {
            return;
        }

        Func<IServiceProvider, IGuideManager>? resolveInner = null;
        if (registered.ImplementationType is { } implementationType)
        {
            // Register the server's implementation under its own type so it is still built
            // (and built only once) by the container, then wrap that instance.
            serviceCollection.AddSingleton(implementationType);
            resolveInner = sp => (IGuideManager)sp.GetRequiredService(implementationType);
        }
        else if (registered.ImplementationInstance is IGuideManager instance)
        {
            resolveInner = _ => instance;
        }
        else if (registered.ImplementationFactory is { } factory)
        {
            resolveInner = sp => (IGuideManager)factory(sp);
        }

        if (resolveInner is null)
        {
            return;
        }

        serviceCollection.Remove(registered);
        serviceCollection.AddSingleton<IGuideManager>(sp => new RepairingGuideManager(
            resolveInner(sp),
            sp.GetRequiredService<ProgramImageRepair>()));
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
