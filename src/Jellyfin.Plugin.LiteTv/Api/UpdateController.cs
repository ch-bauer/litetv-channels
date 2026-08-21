using System.Globalization;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LiteTv.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// The update server for the Wholphin fork.
/// <para>
/// The television gets its builds over ADB today, which means ADB stays switched on for no
/// other reason - a standing remote-access path kept open purely so an APK can be pushed. The
/// app can already update itself: it fetches an <c>Update URL</c> from its settings, expects
/// the shape GitHub's releases API answers with, and installs the asset it finds there. That
/// URL is just a URL, so the plugin can be the thing at the other end of it. The television
/// already talks to this server constantly, so nothing new has to be reachable, no repository
/// has to be public, and no token has to live on the box.
/// </para>
/// <para>
/// What the app requires, measured against <c>UpdateChecker.kt</c> and kept exactly:
/// <c>name</c> must parse as <c>v1.0.5-22-g7b77227d</c> and nothing else, because the app
/// matches the whole string; <c>assets</c> must carry a <c>name</c> the app asks for by name -
/// <c>Wholphin-release-armeabi-v7a.apk</c> first, then <c>Wholphin-release.apk</c>, then
/// <c>Wholphin.apk</c> - and a <c>browser_download_url</c> to fetch it from. Everything else in
/// a GitHub release is decoration and is answered anyway so the shape stays recognisable.
/// </para>
/// </summary>
[ApiController]
[Route("LiteTv/Update")]
public class UpdateController : ControllerBase
{
    /// <summary>
    /// Answers with the newest build, in the shape the app's update check expects.
    /// <para>
    /// Open, because the check that reads it is: the app makes this request on its plain HTTP
    /// client, before and regardless of any Jellyfin login, and an update it cannot ask about is
    /// an update it will never install. The same is true of the download. What that exposes is
    /// the fork's own APK to anyone who can already reach this server - which on the network
    /// this is built for is the household, and which is exactly what the ADB port it replaces
    /// was exposing to the same people, unauthenticated, in both directions.
    /// </para>
    /// </summary>
    /// <returns>The newest release, or 404 when no build has been uploaded.</returns>
    [HttpGet("latest")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ReleaseDto> GetLatest()
    {
        var sameVersion = Newest();
        if (sameVersion.Count == 0)
        {
            return NotFound();
        }

        var newest = sameVersion[0];
        return new ReleaseDto
        {
            Name = newest.Version.ToString(),
            TagName = newest.Version.ToString(),
            PublishedAt = newest.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Body = Notes(newest),
            Assets = Assets(sameVersion)
        };
    }

    /// <summary>
    /// Serves a build.
    /// <para>
    /// Under the name the app asked for rather than the name on disk: the app picks an asset by
    /// its exact name and then fetches the address that asset carried, so both halves have to
    /// speak the same names. Range requests are left on - an APK is tens of megabytes over a
    /// household network and a television that loses the connection half way should be able to
    /// carry on rather than start again.
    /// </para>
    /// </summary>
    /// <param name="name">The asset name, as advertised by <c>latest</c>.</param>
    /// <returns>The APK, or 404 if no stored build answers to that name.</returns>
    [HttpGet("Download/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDownload([FromRoute] string name)
    {
        var builds = Builds();
        var sameVersion = Newest();
        if (builds.Count == 0)
        {
            return NotFound();
        }

        var match = sameVersion.Count == 0 ? null : Assets(sameVersion)
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        // A file asked for by the name it has on disk is served too. Nothing the app does needs
        // that; a person copying a link out of the builds list does.
        var path = match is not null
            ? sameVersion.First(b => string.Equals(b.FileName, match.FileName, StringComparison.Ordinal)).Path
            : builds.FirstOrDefault(b => string.Equals(b.FileName, name, StringComparison.OrdinalIgnoreCase))?.Path;

        return path is not null && System.IO.File.Exists(path)
            ? PhysicalFile(path, "application/vnd.android.package-archive", enableRangeProcessing: true)
            : NotFound();
    }

    /// <summary>
    /// Lists the builds the update server is holding.
    /// </summary>
    /// <returns>Every stored build, newest version first.</returns>
    [HttpGet("Builds")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BuildListDto> GetBuilds()
    {
        var builds = Builds().OrderByDescending(b => b.Version).ThenBy(b => b.FileName, StringComparer.Ordinal).ToList();
        var offered = GetLatest().Value;

        return new BuildListDto
        {
            LatestVersion = offered?.Name,
            UpdateUrl = Absolute("/LiteTv/Update/latest"),
            Builds = builds.Select(b => new BuildDto
            {
                FileName = b.FileName,
                Version = b.Version.ToString(),
                BuildType = b.BuildType,
                Abi = b.Abi,
                Bytes = b.Bytes,
                Modified = b.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Offered = offered is not null
                    && offered.Assets.Any(a => string.Equals(a.FileName, b.FileName, StringComparison.Ordinal))
            }).ToList()
        };
    }

    /// <summary>
    /// Takes a build, so the television never has to be plugged into anything again.
    /// <para>
    /// The APK is uploaded whole and moved into place, so an upload that dies half way leaves
    /// the previous build intact rather than offering the television a truncated file. Notes are
    /// kept beside it under the same name, and are what the app shows on its update screen.
    /// </para>
    /// </summary>
    /// <param name="fileName">The APK's file name, as Gradle wrote it.</param>
    /// <param name="notes">Optional release notes, shown by the app before installing.</param>
    /// <returns>What the store now holds.</returns>
    [HttpPost("Builds/{fileName}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(512L * 1024 * 1024)]
    public async Task<ActionResult<BuildListDto>> PostBuild([FromRoute] string fileName, [FromQuery] string? notes)
    {
        var directory = UpdateDirectory();
        if (directory is null)
        {
            return BadRequest("the plugin has nowhere to store builds");
        }

        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe)
            || !safe.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(safe, fileName, StringComparison.Ordinal))
        {
            return BadRequest("that is not an .apk file name");
        }

        if (AppBuild.Describe(safe, 0, DateTime.UtcNow, string.Empty) is null)
        {
            return BadRequest("no version could be read from that file name - the app compares versions, so a build it cannot read is a build it can never offer");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, safe);
        var temporary = path + ".part";

        try
        {
            await using (var file = System.IO.File.Create(temporary))
            {
                await Request.Body.CopyToAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            if (new FileInfo(temporary).Length == 0)
            {
                System.IO.File.Delete(temporary);
                return BadRequest("that upload was empty");
            }

            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (System.IO.File.Exists(temporary))
            {
                System.IO.File.Delete(temporary);
            }

            return BadRequest($"could not store that build: {ex.Message}");
        }

        var notesPath = path + ".md";
        if (!string.IsNullOrWhiteSpace(notes))
        {
            await System.IO.File.WriteAllTextAsync(notesPath, notes, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        else if (System.IO.File.Exists(notesPath))
        {
            // Notes belong to the build that was here before, and a new build with the old
            // build's notes reads as a lie about what changed.
            System.IO.File.Delete(notesPath);
        }

        return GetBuilds();
    }

    /// <summary>
    /// Removes a stored build.
    /// </summary>
    /// <param name="fileName">The APK to remove.</param>
    /// <returns>What the store holds afterwards.</returns>
    [HttpDelete("Builds/{fileName}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BuildListDto> DeleteBuild([FromRoute] string fileName)
    {
        var directory = UpdateDirectory();
        var safe = Path.GetFileName(fileName);
        if (directory is null || string.IsNullOrWhiteSpace(safe) || !string.Equals(safe, fileName, StringComparison.Ordinal))
        {
            return NotFound();
        }

        var path = Path.Combine(directory, safe);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        System.IO.File.Delete(path);
        if (System.IO.File.Exists(path + ".md"))
        {
            System.IO.File.Delete(path + ".md");
        }

        return GetBuilds();
    }

    /// <summary>The asset list for a release, as JSON.</summary>
    private List<AssetDto> Assets(IReadOnlyCollection<AppBuild> builds) =>
        AppBuild.Assets(builds).Select(pair => new AssetDto
        {
            Name = pair.Key,
            FileName = pair.Value.FileName,
            Size = pair.Value.Bytes,
            ContentType = "application/vnd.android.package-archive",
            DownloadUrl = Absolute($"/LiteTv/Update/Download/{Uri.EscapeDataString(pair.Key)}")
        }).ToList();

    /// <summary>The builds of the version currently on offer.</summary>
    private static IReadOnlyList<AppBuild> Newest() => AppBuild.Newest(Builds());

    /// <summary>Everything the store holds that can be read as a build.</summary>
    private static List<AppBuild> Builds()
    {
        var directory = UpdateDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return new List<AppBuild>();
        }

        return Directory.EnumerateFiles(directory, "*.apk")
            .Select(path =>
            {
                var info = new FileInfo(path);
                return AppBuild.Describe(info.Name, info.Length, info.LastWriteTimeUtc, path);
            })
            .Where(b => b is not null)
            .Select(b => b!)
            .ToList();
    }

    /// <summary>The notes stored beside a build, or a plain statement of what it is.</summary>
    private static string Notes(AppBuild build)
    {
        var sidecar = build.Path + ".md";
        if (System.IO.File.Exists(sidecar))
        {
            var text = System.IO.File.ReadAllText(sidecar);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{build.Version} — served by the LiteTV plugin on this server.\n\n`{build.FileName}`");
    }

    /// <summary>
    /// Makes an address the television can fetch.
    /// <para>
    /// Built from the request rather than from configuration: the app reached this server at
    /// some address it already knows works, and any address written down here would be whichever
    /// hostname the dashboard happened to be open on - the same trap channel artwork fell into.
    /// </para>
    /// </summary>
    private string Absolute(string path) =>
        string.Concat(Request.Scheme, "://", Request.Host.ToUriComponent(), Request.PathBase.ToUriComponent(), path);

    private static string? UpdateDirectory()
    {
        var data = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrEmpty(data) ? null : Path.Combine(data, "updates");
    }

}

/// <summary>A release, in the shape GitHub's API answers with.</summary>
public class ReleaseDto
{
    /// <summary>Gets or sets the version, which is the only field the app insists on parsing.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the tag. Decoration; nothing reads it.</summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>Gets or sets when the build was uploaded.</summary>
    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the release notes, shown by the app before it installs.</summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets the downloadable files.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyList<AssetDto> Assets { get; set; } = Array.Empty<AssetDto>();
}

/// <summary>One downloadable file of a release.</summary>
public class AssetDto
{
    /// <summary>Gets or sets the name the app asks for.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets where to fetch it.</summary>
    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets the content type.</summary>
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the file on disk this name is answered by. Ours, not GitHub's.</summary>
    [JsonPropertyName("litetv_file")]
    public string FileName { get; set; } = string.Empty;
}

/// <summary>What the update store holds, for the configuration page.</summary>
public class BuildListDto
{
    /// <summary>Gets or sets the version currently on offer, if any.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Gets or sets the address to put in the app's Update URL setting.</summary>
    public string UpdateUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored builds.</summary>
    public IReadOnlyList<BuildDto> Builds { get; set; } = Array.Empty<BuildDto>();
}

/// <summary>One stored build.</summary>
public class BuildDto
{
    /// <summary>Gets or sets the file name on disk.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the version read out of that name.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets release or debug.</summary>
    public string BuildType { get; set; } = string.Empty;

    /// <summary>Gets or sets the ABI, when the name says.</summary>
    public string? Abi { get; set; }

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets when it was uploaded.</summary>
    public string Modified { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is one of the files the app is being offered.</summary>
    public bool Offered { get; set; }
}
