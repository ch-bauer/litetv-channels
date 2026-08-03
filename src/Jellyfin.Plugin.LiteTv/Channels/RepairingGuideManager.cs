using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Wraps the server's guide manager so a refresh is followed by
/// <see cref="ProgramImageRepair"/>, which completes the image metadata the refresh leaves
/// blank on every programme it writes.
/// <para>
/// A refresh is the only moment programmes are created or have their artwork changed, and the
/// server raises no event for it: <c>LibraryManager</c> deliberately suppresses item added and
/// updated notifications for anything that is not a library item, precisely so a guide full of
/// programmes does not drown everything listening. Finishing the refresh is therefore the one
/// reliable signal that there is something to put right, and following the call is the only
/// way to get it.
/// </para>
/// </summary>
internal sealed class RepairingGuideManager : IGuideManager
{
    private readonly IGuideManager _inner;
    private readonly ProgramImageRepair _repair;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepairingGuideManager"/> class.
    /// </summary>
    /// <param name="inner">The server's guide manager.</param>
    /// <param name="repair">Completes programme image metadata.</param>
    public RepairingGuideManager(IGuideManager inner, ProgramImageRepair repair)
    {
        _inner = inner;
        _repair = repair;
    }

    /// <inheritdoc />
    public GuideInfo GetGuideInfo() => _inner.GetGuideInfo();

    /// <inheritdoc />
    public async Task RefreshGuide(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _inner.RefreshGuide(progress, cancellationToken).ConfigureAwait(false);

        // The refresh itself has succeeded by this point and the caller is told so either
        // way: RunAsync reports its own failures rather than raising them.
        await _repair.RunAsync("a guide refresh", cancellationToken).ConfigureAwait(false);
    }
}
