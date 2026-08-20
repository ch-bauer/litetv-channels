using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Configuration;

/// <summary>
/// Puts LiteTV in the web client's own sidebar, when the server has the plugin that can.
/// <para>
/// Configuring a channel otherwise means Dashboard, then Plugins, then LiteTV, every time -
/// three navigations to reach the only screen anybody actually edits. The Plugin Pages plugin
/// exists to solve exactly this, and it takes its entries from a JSON file in its own
/// configuration folder. That file is the whole contract; there is no API to call.
/// </para>
/// <para>
/// Nothing here is required. A server without Plugin Pages gets no link and no stray file, and
/// the dashboard page it already had is untouched either way.
/// </para>
/// </summary>
public static class SidebarLink
{
    /// <summary>The folder Plugin Pages keeps its configuration in.</summary>
    private const string PluginPagesConfigFolder = "Jellyfin.Plugin.PluginPages";

    /// <summary>
    /// Bumped when the entry below changes in a way an already-registered link would not pick
    /// up. Plugin Pages never revisits an entry once written, so the version is the only way to
    /// replace one.
    /// </summary>
    private const int EntryVersion = 1;

    /// <summary>
    /// Registers the sidebar entry, if Plugin Pages is installed and has not got one already.
    /// </summary>
    /// <param name="paths">The server's application paths.</param>
    /// <param name="logger">Where to say what happened.</param>
    public static void Register(IApplicationPaths paths, ILogger logger)
    {
        try
        {
            if (!IsPluginPagesInstalled(paths))
            {
                logger.LogDebug("LiteTV: no Plugin Pages, so no sidebar link");
                return;
            }

            var file = Path.Combine(paths.PluginConfigurationsPath, PluginPagesConfigFolder, "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var config = Read(file);
            if (config["pages"] is not JsonArray pages)
            {
                pages = new JsonArray();
                config["pages"] = pages;
            }

            // An entry from an older version of this plugin is removed rather than edited: the
            // file belongs to somebody else, and replacing one object wholesale is the only
            // change that cannot half-apply.
            var existing = pages.FirstOrDefault(p => (string?)p?["Id"] == EntryId);
            if (existing is not null)
            {
                if ((int?)existing["Version"] >= EntryVersion)
                {
                    return;
                }

                pages.Remove(existing);
            }

            pages.Add(new JsonObject
            {
                ["Id"] = EntryId,
                ["Url"] = "/LiteTv/Page",
                ["DisplayText"] = "LiteTV",
                ["Icon"] = "live_tv",
                ["Version"] = EntryVersion
            });

            File.WriteAllText(file, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            logger.LogInformation("LiteTV: added itself to the web client sidebar via Plugin Pages");
        }
        catch (Exception ex)
        {
            // A sidebar link is a convenience. Nothing about the plugin depends on it, and a
            // plugin that refused to load because somebody else's config file was malformed
            // would be trading something that matters for something that does not.
            logger.LogWarning(ex, "LiteTV: could not register the sidebar link");
        }
    }

    private static string EntryId => typeof(Plugin).Namespace!;

    private static JsonObject Read(string file)
    {
        if (!File.Exists(file))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(file);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Whether Plugin Pages is on this server.
    /// <para>
    /// Asked of the plugins folder rather than of the loaded assemblies, because plugins load
    /// in name order and "LiteTv" comes before "PluginPages" - so at the moment this runs the
    /// assembly reliably is not there yet, and looking for it would answer no every time.
    /// </para>
    /// </summary>
    private static bool IsPluginPagesInstalled(IApplicationPaths paths)
    {
        if (Directory.Exists(Path.Combine(paths.PluginConfigurationsPath, PluginPagesConfigFolder)))
        {
            return true;
        }

        return Directory.Exists(paths.PluginsPath)
            && Directory.EnumerateDirectories(paths.PluginsPath)
                .Select(Path.GetFileName)
                .Any(name => name is not null
                    && name.Replace(" ", string.Empty).Contains("PluginPages", StringComparison.OrdinalIgnoreCase));
    }
}
