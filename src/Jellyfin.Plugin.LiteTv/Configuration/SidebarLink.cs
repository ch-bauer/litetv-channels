using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Configuration;

/// <summary>
/// Takes LiteTV back out of the web client's own sidebar.
/// <para>
/// For a while the plugin put itself there through the Plugin Pages plugin, so that configuring
/// a channel did not mean Dashboard, then Plugins, then LiteTV. That link is in every user's
/// sidebar, not only an administrator's - and what it opens is the configuration page, where
/// somebody who is not an administrator sees every channel, every source and the name of the
/// playback account, and cannot save any of it. The page belongs in the dashboard, which is
/// where <see cref="Plugin.GetPages"/> now asks for it: <c>EnableInMainMenu</c> puts it in the
/// dashboard's own menu beside the other plugins, and the dashboard is administrators only.
/// </para>
/// <para>
/// This runs on every start rather than once, because Plugin Pages' configuration file is not
/// ours and an entry written by an older version of this plugin would otherwise sit in it for
/// good - the plugin that put it there being gone is not something Plugin Pages checks.
/// </para>
/// </summary>
public static class SidebarLink
{
    /// <summary>The folder Plugin Pages keeps its configuration in.</summary>
    private const string PluginPagesConfigFolder = "Jellyfin.Plugin.PluginPages";

    /// <summary>
    /// Removes the sidebar entry this plugin used to register, if it is still there.
    /// </summary>
    /// <param name="paths">The server's application paths.</param>
    /// <param name="logger">Where to say what happened.</param>
    public static void Remove(IApplicationPaths paths, ILogger logger)
    {
        try
        {
            var file = Path.Combine(paths.PluginConfigurationsPath, PluginPagesConfigFolder, "config.json");
            if (!File.Exists(file))
            {
                return;
            }

            var text = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(text) || JsonNode.Parse(text) is not JsonObject config)
            {
                return;
            }

            if (config["pages"] is not JsonArray pages)
            {
                return;
            }

            var ours = pages.Where(p => (string?)p?["Id"] == EntryId).ToList();
            if (ours.Count == 0)
            {
                return;
            }

            foreach (var entry in ours)
            {
                pages.Remove(entry);
            }

            File.WriteAllText(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            logger.LogInformation("LiteTV: removed itself from the web client sidebar; it is in the dashboard menu instead");
        }
        catch (Exception ex)
        {
            // Tidying up somebody else's configuration file is not worth failing to load over.
            logger.LogWarning(ex, "LiteTV: could not remove the sidebar link");
        }
    }

    private static string EntryId => typeof(Plugin).Namespace!;
}
