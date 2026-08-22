using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv;

/// <summary>
/// The LiteTV Channels plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Here rather than in a startup task because it is a one-line file edit that has to
        // have happened before the web client asks Plugin Pages what to draw, and the plugin
        // constructor is the earliest point at which the paths are known.
        SidebarLink.Remove(applicationPaths, logger);
    }

    /// <inheritdoc />
    public override string Name => "LiteTV Channels";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("13953c97-f5a0-4713-8d4c-96b5369e5791");

    /// <inheritdoc />
    public override string Description =>
        "Lightweight virtual TV channels: deterministic schedules over your own library, tuned in via normal direct playback at the live position. No transcoding, no tuner emulation.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Keeps the proof-of-origin token when something saves a configuration that has forgotten
    /// it.
    /// <para>
    /// The configuration page posts back the whole configuration as it was when the page
    /// loaded. Open it before a television has minted, save it afterwards for some unrelated
    /// reason, and the stored token is gone - not because anybody edited it, but because the
    /// page never knew about it. The token is not on the page at all for that reason; this is
    /// the other half, for every other writer of the configuration.
    /// </para>
    /// <para>
    /// Only ever fills a gap. A save that carries a token is taken as it is, which is what lets
    /// <see cref="Trailers.ProofOfOrigin"/> store a new one and clear an old one.
    /// </para>
    /// </summary>
    /// <param name="configuration">The configuration being saved.</param>
    public override void UpdateConfiguration(MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming
            && string.IsNullOrEmpty(incoming.ProofOfOriginToken)
            && Trailers.ProofOfOrigin.Held is { } held)
        {
            incoming.ProofOfOriginToken = held.StreamToken;
            incoming.ProofOfOriginVisitorData = held.VisitorData;
            incoming.ProofOfOriginMintedUtc = held.MintedUtc;
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),

                // In the dashboard's own left-hand menu, not only behind Dashboard → Plugins →
                // LiteTV. This is the screen that gets edited - channels, artwork, the schedule -
                // and three navigations to reach it is three too many. Jellyfin has always had
                // this and the plugin simply never asked.
                // No MenuSection: every plugin on this server that appears in that menu - Intro
                // Skipper, Jellyfin Enhanced, JS Injector, Segment Editor - leaves it unset, and
                // matching them is the point.
                EnableInMainMenu = true,
                DisplayName = "LiteTV",
                MenuIcon = "live_tv"
            }
        };
    }
}
