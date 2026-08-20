using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Keeps the published "TV-Sender" channel out of My Media on clients that would otherwise
/// list it as an ordinary library.
/// <para>
/// A channel is not a library. Left in My Media it sits between the film and series folders
/// on every client and every device, offering a flat list of entries that are only
/// meaningful as a schedule - and on clients the plugin cannot reach, opening one plays a
/// programme rather than tuning in. Hiding it leaves the channels reachable exactly where
/// they make sense: the web client's own row and guide, and the TV app, which asks for
/// hidden views on purpose.
/// </para>
/// <para>
/// The mechanism is Jellyfin's own per-user "exclude from My Media" list, which is why this
/// works on every client at once rather than only where a script can be injected: the server
/// simply stops listing the view. Anything asking for hidden views included
/// (<c>/UserViews?includeHidden=true</c>) still sees it.
/// </para>
/// </summary>
public sealed class MyMediaVisibility : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MyMediaVisibility> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MyMediaVisibility"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">Used to find the published channel's own view.</param>
    /// <param name="logger">The logger.</param>
    public MyMediaVisibility(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<MyMediaVisibility> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        // Applied at startup as well as on save: the channel view does not exist until the
        // server has indexed the channel, so a setting saved before that had nothing to
        // hide, and a server that starts with the setting already on must honour it.
        //
        // Off the startup thread and behind its own guard. A plugin has no business being
        // able to stop the server booting, and the try inside ApplyAsync is not enough on its
        // own: a method that cannot be resolved - an API whose shape changed in the server
        // this is actually running on - throws as its state machine starts, which is before
        // its own try block exists. Only the caller can catch that.
        _ = Task.Run(SafeApplyAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task SafeApplyAsync()
    {
        try
        {
            await ApplyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not update whether the channel shows in My Media.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        => _ = Task.Run(SafeApplyAsync, CancellationToken.None);

    /// <summary>
    /// Brings every user's exclusion list in line with the setting.
    /// </summary>
    public async Task ApplyAsync()
    {
        try
        {
            var hide = Plugin.Instance?.Configuration.HideChannelFromMyMedia ?? false;
            var viewId = ChannelViewId();
            if (viewId is null)
            {
                // Nothing published yet. Turning the setting on before the channel exists is
                // ordinary, and the startup pass will catch it next time round.
                if (hide)
                {
                    _logger.LogDebug("LiteTV: no channel view to hide from My Media yet.");
                }

                return;
            }

            var id = viewId.Value;
            foreach (var user in _userManager.GetUsers())
            {
                var config = _userManager.GetUserDto(user).Configuration;
                var excludes = config.MyMediaExcludes ?? Array.Empty<Guid>();
                var has = Array.IndexOf(excludes, id) >= 0;
                if (has == hide)
                {
                    continue;
                }

                // Only this one entry is ever touched: whatever else the viewer chose to keep
                // out of My Media is theirs and stays as it is.
                config.MyMediaExcludes = hide
                    ? excludes.Append(id).ToArray()
                    : excludes.Where(e => e != id).ToArray();

                await _userManager.UpdateConfigurationAsync(user.Id, config).ConfigureAwait(false);
                _logger.LogInformation(
                    "LiteTV: the channel is now {State} in My Media for {User}.",
                    hide ? "hidden" : "shown",
                    user.Username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not update whether the channel shows in My Media.");
        }
    }

    /// <summary>
    /// The id of the view the published channel appears as. Looked up by name rather than
    /// derived, so it does not depend on how the server happens to compute channel ids.
    /// </summary>
    private Guid? ChannelViewId()
    {
        var name = LiteTvChannelProvider.ChannelName;
        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Channel }
        });

        foreach (var channel in channels)
        {
            if (string.Equals(channel.Name, name, StringComparison.Ordinal))
            {
                return channel.Id;
            }
        }

        return null;
    }
}
