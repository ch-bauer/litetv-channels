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
        SidebarLink.Register(applicationPaths, logger);
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
                    GetType().Namespace)
            }
        };
    }
}
