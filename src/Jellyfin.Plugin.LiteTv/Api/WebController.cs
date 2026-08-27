using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// Serves the configuration app's built bundle out of the assembly.
/// <para>
/// The configuration page used to be one large hand-written HTML file wearing the dashboard's
/// own components, and most of what was wrong with it was wrong because of that: the theme, the
/// component upgrade and the form width cap all had opinions, and fighting them is what made the
/// page diverge from its design. The app is built by Vite instead - it brings its own markup and
/// its own CSS - and the built files are embedded here and served from this one route. It is the
/// shape Segment Editor uses for the same reason.
/// </para>
/// <para>
/// The build writes a <b>flat</b> directory: one stable entry, <c>litetv.js</c>, which
/// <c>configPage.html</c> names in a script tag, and content-hashed names for everything else.
/// Flat matters - each file is embedded under a logical name built from its filename alone, so
/// there are no path separators to carry through MSBuild metadata and unpick again here.
/// </para>
/// </summary>
[ApiController]
[Route("LiteTv/Web")]
public class WebController : ControllerBase
{
    /// <summary>The logical-name prefix the csproj embeds these files under.</summary>
    private const string Prefix = "litetv.web.";

    private static readonly Assembly OwnAssembly = typeof(WebController).Assembly;

    /// <summary>
    /// Content types for what a Vite build actually emits. Anything unrecognised is served as
    /// <c>application/octet-stream</c>, which a browser will decline to execute - deliberately:
    /// guessing at a type for a file this build should never have produced is how a bundle ends
    /// up half-working in a way nobody can explain.
    /// </summary>
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
    };

    /// <summary>
    /// Serves one file from the bundle.
    /// </summary>
    /// <param name="path">The file's name within the bundle, such as <c>litetv.js</c>.</param>
    /// <returns>The file, or 404 if the bundle holds no such name.</returns>
    [HttpGet("{**path}")]
    // Anonymous of necessity, not convenience: a <script src> cannot carry an Authorization
    // header, so a bundle behind authentication cannot be loaded by the page that needs it.
    // What this exposes is the app's own client code - the same code any signed-in user would
    // be served - and no configuration, no library data and no token. Everything the app then
    // asks for goes through the authenticated LiteTv endpoints as before.
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(string path)
    {
        // The build is flat, so a name with any path in it cannot be one of ours. Rejecting it
        // here also settles traversal: there is nothing to traverse to.
        if (string.IsNullOrWhiteSpace(path)
            || Path.GetFileName(path) != path
            || path.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        var stream = OwnAssembly.GetManifestResourceStream(Prefix + path);
        if (stream is null)
        {
            return NotFound();
        }

        var extension = Path.GetExtension(path);
        var type = ContentTypes.TryGetValue(extension, out var known)
            ? known
            : "application/octet-stream";

        // Everything but the entry carries a content hash in its name, so it can be cached for
        // as long as the browser likes: a new build produces new names. The entry's name never
        // changes and therefore must never be cached, or an updated plugin keeps serving the
        // previous app until someone clears their browser - which would look exactly like the
        // update having silently failed.
        Response.Headers.CacheControl = IsEntry(path)
            ? "no-cache, no-store, must-revalidate"
            : "public, max-age=31536000, immutable";

        return File(stream, type);
    }

    /// <summary>
    /// The two files <c>configPage.html</c> names in static tags. They keep stable names so the
    /// page can name them at all, which is exactly why they must not be cached.
    /// </summary>
    private static bool IsEntry(string path)
        => string.Equals(path, "litetv.js", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "litetv.css", StringComparison.OrdinalIgnoreCase);
}
