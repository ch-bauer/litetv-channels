using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.LiteTv.Integrations;

/// <summary>
/// Says which of the plugins LiteTV can lean on are actually installed, and in what
/// state. Asked by GUID, never by name and never by firing a request at an endpoint:
/// a 404 cannot tell "not installed" from "installed but broken", and a plugin that
/// is present but disabled or malfunctioning has to read differently from an absent
/// one - the first is a switch to flick, the second is something to go and install.
/// </summary>
public class SiblingPlugins
{
    /// <summary>The plugins LiteTV asks about, in the order they are worth reporting.</summary>
    /// <remarks>
    /// Enhanced Poster Tags was here and was taken out on the owner's instruction: LiteTV does
    /// not ask it for anything, so reporting it only put a plugin on the page that nothing on
    /// the page uses. Do not add it back.
    /// </remarks>
    private static readonly SiblingDefinition[] Known =
    {
        new("61b616fa-7ba8-4262-b2a9-fae29b015930", "Smart Similar", "Scores suggestions. Without it LiteTV falls back to a much rougher genre match."),
        new("b9f0c474-e1a9-4a06-9c8a-3f1d2e5b7a10", "Collection Row", "Shows a film's collections on the detail page in the TV app."),
        new("b7c3f1e2-9a4d-4e8b-b0c6-2f5d8a913c47", "FSK Rating Updater", "Normalises age ratings so the playback badge has something to draw."),
        new("684a50b4-3970-44ef-aab0-3a162b415374", "SponsorBlock Segments", "Trims sponsor and outro stretches off linked trailers."),
        new("64c04809-0078-401f-a883-2fc0fddace8c", "Next Up Cleanup", "Keeps Continue Watching tidy, so a channel that was tuned out of does not sit there as an unfinished programme."),
        new("c3cbb73c-59e6-4ec6-9cba-a86ba70e73c0", "Clear Transcodes", "Clears leftover transcode files. LiteTV never asks for a transcode, but the rest of the server does.")
    };

    private readonly IPluginManager _pluginManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SiblingPlugins"/> class.
    /// </summary>
    /// <param name="pluginManager">The server's plugin manager.</param>
    public SiblingPlugins(IPluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    /// <summary>Gets Smart Similar's plugin id.</summary>
    public static Guid SmartSimilarId { get; } = new("61b616fa-7ba8-4262-b2a9-fae29b015930");

    /// <summary>Gets Collection Row's plugin id.</summary>
    public static Guid CollectionRowId { get; } = new("b9f0c474-e1a9-4a06-9c8a-3f1d2e5b7a10");

    /// <summary>Gets the FSK Rating Updater's plugin id.</summary>
    public static Guid FskRatingsId { get; } = new("b7c3f1e2-9a4d-4e8b-b0c6-2f5d8a913c47");

    /// <summary>Reports every plugin LiteTV knows how to use.</summary>
    /// <returns>One row per known plugin, installed or not.</returns>
    public IReadOnlyList<SiblingPluginStatus> All()
    {
        var installed = Installed();
        var rows = new List<SiblingPluginStatus>(Known.Length);

        foreach (var definition in Known)
        {
            installed.TryGetValue(definition.Id, out var plugin);
            rows.Add(new SiblingPluginStatus
            {
                Id = definition.Id.ToString(),
                Name = plugin?.Name ?? definition.Name,
                Installed = plugin != null,
                Version = plugin?.Version?.ToString(),
                Status = plugin?.Manifest?.Status.ToString(),

                // A plugin only counts as usable when it is loaded and active: a
                // malfunctioning or disabled one answers nothing, and asking it
                // anyway is how a page ends up waiting on a request that cannot work.
                Usable = IsUsable(plugin),
                WhyItMatters = definition.WhyItMatters
            });
        }

        return rows;
    }

    /// <summary>Answers whether one plugin is installed and in a state to answer requests.</summary>
    /// <param name="id">The plugin's GUID.</param>
    /// <returns>True when it can be called.</returns>
    public bool IsUsable(Guid id)
    {
        return Installed().TryGetValue(id, out var plugin) && IsUsable(plugin);
    }

    private static bool IsUsable(LocalPlugin? plugin)
    {
        if (plugin == null)
        {
            return false;
        }

        // Status is nullable in practice for a plugin loaded outside the manifest
        // flow; treat "no manifest" as usable, since it is loaded either way.
        var status = plugin.Manifest?.Status;
        return status == null || status == MediaBrowser.Model.Plugins.PluginStatus.Active;
    }

    private Dictionary<Guid, LocalPlugin> Installed()
    {
        var map = new Dictionary<Guid, LocalPlugin>();
        foreach (var plugin in _pluginManager.Plugins)
        {
            // Several versions of the same plugin can sit side by side; the highest
            // one is what the server loads, so it is the one worth reporting.
            if (!map.TryGetValue(plugin.Id, out var existing) || plugin.Version > existing.Version)
            {
                map[plugin.Id] = plugin;
            }
        }

        return map;
    }

    private sealed record SiblingDefinition(Guid Id, string Name, string WhyItMatters)
    {
        public SiblingDefinition(string id, string name, string whyItMatters)
            : this(new Guid(id), name, whyItMatters)
        {
        }
    }
}

/// <summary>
/// One plugin LiteTV can use, and whether it is there.
/// </summary>
public class SiblingPluginStatus
{
    /// <summary>Gets or sets the plugin's GUID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is installed at all.</summary>
    public bool Installed { get; set; }

    /// <summary>Gets or sets the installed version, when it is installed.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the server's own word for its state: Active, Disabled, Malfunctioned...</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets a value indicating whether it is installed <b>and</b> able to answer.</summary>
    public bool Usable { get; set; }

    /// <summary>Gets or sets one line on what LiteTV loses without it.</summary>
    public string WhyItMatters { get; set; } = string.Empty;
}
