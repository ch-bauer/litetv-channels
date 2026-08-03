using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Channels;

/// <summary>
/// Fills in the image metadata the server leaves blank on Live TV programmes, without which
/// their artwork never reaches a browser.
/// <para>
/// A programme's artwork is handed over as a path to a file that is already on this server,
/// and the server records it by hand: <c>GuideManager.UpdateImage</c> builds the stored image
/// info from the path and the type alone, leaving the modification date and the pixel
/// dimensions at zero. Channels do not go the same way - theirs is recorded by
/// <c>BaseItem.SetImagePath</c>, which reads both off the file - and that difference is the
/// whole reason channel logos appear in the guide while programme posters do not.
/// </para>
/// <para>
/// Two things go wrong once those fields are zero, and both are invisible from here:
/// </para>
/// <list type="number">
/// <item><description>
/// With no dimensions, the image pipeline cannot tell whether the file already fits what was
/// asked for, and its "everything is default, hand back the original" test does not consider
/// the fill width and height a browser actually sends. So every request is answered with the
/// full-size original - for a poster, megabytes of it, per programme, per guide page.
/// </description></item>
/// <item><description>
/// Taking that shortcut also means the response keeps the zero modification date, which
/// reaches the client as <c>Last-Modified: 01 Jan 0001</c>. The image endpoint then compares
/// that against the request's <c>If-Modified-Since</c> - absent on a first load, so it too
/// counts as the zero date - decides nothing has changed since, and answers <c>304 Not
/// Modified</c> with an empty body. The browser is told to use a cached copy it has never
/// been given, so the card simply stays blank. This only happens when the URL carries an
/// image tag, which is exactly what the web client puts on every one of them.
/// </description></item>
/// </list>
/// <para>
/// Both follow from the same two blank fields, so filling them in is the whole fix: the
/// artwork is resized as intended and served with a date that means something. The values
/// written here are read off the file the server itself is pointing at, which is the same
/// thing it would have recorded had the programme gone through <c>SetImagePath</c>.
/// </para>
/// <para>
/// This runs after every guide refresh, and once at startup so an existing guide is put right
/// without waiting for the next one. Programmes whose metadata is already sound are skipped
/// before anything touches the disk, which is what keeps the repeat passes cheap.
/// </para>
/// </summary>
public class ProgramImageRepair : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ProgramImageRepair> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramImageRepair"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="imageProcessor">Reads the pixel dimensions of an image file.</param>
    /// <param name="fileSystem">Reads the modification date of an image file.</param>
    /// <param name="logger">The logger.</param>
    public ProgramImageRepair(
        ILibraryManager libraryManager,
        IImageProcessor imageProcessor,
        IFileSystem fileSystem,
        ILogger<ProgramImageRepair> logger)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Detached on purpose: the guide can hold thousands of programmes, and nothing about
        // the server needs to wait for their artwork to be sorted out before it comes up.
        _ = Task.Run(() => RunAsync("startup", CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Repairs the stored image metadata of every LiteTV programme that needs it, and reports
    /// what it did. Failure is logged rather than raised: this improves how artwork is served
    /// and is never the reason a guide refresh should be considered to have failed.
    /// </summary>
    /// <param name="reason">What prompted the pass, for the log.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of programmes whose image metadata was completed.</returns>
    public async Task<int> RunAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            var repaired = await RepairAsync(cancellationToken).ConfigureAwait(false);
            if (repaired > 0)
            {
                _logger.LogInformation(
                    "LiteTV: completed the image metadata of {Count} programme(s) after {Reason}, so their artwork can be resized and served.",
                    repaired,
                    reason);
            }

            return repaired;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiteTV: could not repair programme image metadata after {Reason}.", reason);
            return 0;
        }
    }

    private async Task<int> RepairAsync(CancellationToken cancellationToken)
    {
        // Only this plugin's own channels. Another Live TV provider supplying local paths has
        // the identical problem, but its programmes are not this plugin's to rewrite.
        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        })
            .OfType<LiveTvChannel>()
            .Where(c => string.Equals(c.ServiceName, LiteTvLiveService.ServiceName, StringComparison.Ordinal))
            .ToList();

        var repaired = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var programs = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                ChannelIds = new[] { channel.Id }
            });

            var changed = new List<BaseItem>();
            foreach (var program in programs)
            {
                if (Complete(program))
                {
                    changed.Add(program);
                }
            }

            if (changed.Count == 0)
            {
                continue;
            }

            // The channel is the parent the server itself saves these under.
            await _libraryManager
                .UpdateItemsAsync(changed, channel, ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);

            repaired += changed.Count;
        }

        return repaired;
    }

    /// <summary>
    /// Completes one programme's stored image metadata from the files it points at, and
    /// reports whether anything was actually filled in.
    /// </summary>
    private bool Complete(BaseItem program)
    {
        var changed = false;

        foreach (var image in program.ImageInfos)
        {
            // A remote image is downloaded and re-recorded by the server's own pre-caching
            // pass, which stamps it properly; until then there is no local file to read.
            if (!image.IsLocalFile)
            {
                continue;
            }

            if (image.DateModified != default && image.Width > 0 && image.Height > 0)
            {
                continue;
            }

            var file = _fileSystem.GetFileInfo(image.Path);
            if (!file.Exists)
            {
                // The programme outlived the artwork it was pointing at. Leaving the entry
                // alone keeps this to a metadata repair; removing images is the server's call.
                continue;
            }

            if (image.DateModified == default)
            {
                image.DateModified = _fileSystem.GetLastWriteTimeUtc(file);
                changed = true;
            }

            if (image.Width <= 0 || image.Height <= 0)
            {
                try
                {
                    // Writes the dimensions into the image info as it reads them off the file.
                    _imageProcessor.GetImageDimensions(program, image);
                    changed |= image.Width > 0 && image.Height > 0;
                }
                catch (Exception ex)
                {
                    // Unreadable or not really an image. The date alone is enough to stop the
                    // empty 304, so this is worth recording but not worth giving up over.
                    _logger.LogDebug(ex, "LiteTV: could not read the size of {Path}.", image.Path);
                }
            }
        }

        return changed;
    }
}
