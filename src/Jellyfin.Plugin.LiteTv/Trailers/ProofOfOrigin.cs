using System.Globalization;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// The proof-of-origin token the television mints, and which the server borrows.
/// <para>
/// <b>Why this exists.</b> Measured on 22 Aug 2026: asked anonymously, YouTube hands this server
/// exactly two things - a 1080p ladder whose streams stop serving after about sixty seconds of
/// playback, or one muxed 360p that plays to the end. Nothing else. The sixty-second wall is
/// <c>&amp;pot=</c> enforcement on the stream URLs, and the only thing that lifts it is a PO
/// token.
/// </para>
/// <para>
/// <b>Why the television mints it.</b> A PO token comes out of Google's BotGuard, which is
/// obfuscated JavaScript that expects a browser: a DOM, timers, the lot. A Jellyfin server has
/// no browser, so every server-side answer to this means running one - a Node container beside
/// Jellyfin, or a JavaScript engine and a DOM shim inside the plugin. An Android television box
/// already has a browser engine, because Android ships a WebView. So the box runs BotGuard, and
/// posts the result here.
/// </para>
/// <para>
/// <b>What that costs, stated plainly.</b> The server can only use a token while a television
/// has recently given it one. Nothing here mints anything; with no token this falls back to
/// exactly what happened before, which is the sixty-second ladder or 360p. That is the accepted
/// trade: the box is the thing being watched.
/// </para>
/// </summary>
public static class ProofOfOrigin
{
    /// <summary>
    /// How long a minted token is trusted.
    /// <para>
    /// Six hours. A PO token is generally good for around half a day, and the cost of holding a
    /// stale one is a rung that fails and falls through, so this errs short rather than long.
    /// The television refreshes on every launch anyway.
    /// </para>
    /// </summary>
    private static readonly TimeSpan GoodFor = TimeSpan.FromHours(6);

    private static Minted? _held;

    /// <summary>
    /// What a television minted: the identity it was minted against, and the tokens themselves.
    /// </summary>
    /// <param name="VisitorData">
    /// The visitor id BotGuard was run against. <b>The token is bound to this</b>, so the same
    /// value has to go into the player request - a token minted against one visitor id and sent
    /// with another is refused, and looks exactly like no token at all.
    /// </param>
    /// <param name="StreamToken">The token that goes on the stream URL as <c>pot</c>.</param>
    /// <param name="PlayerToken">
    /// The token for the player request itself, when the minter produced a separate one. YouTube
    /// treats the two as different contexts; sending the stream token where the player one
    /// belongs is worse than sending nothing, so this stays null unless it was really minted.
    /// </param>
    /// <param name="MintedUtc">When it was minted.</param>
    public sealed record Minted(string VisitorData, string StreamToken, string? PlayerToken, DateTime MintedUtc);

    /// <summary>Gets what is held, or null when nothing has been minted lately.</summary>
    public static Minted? Held =>
        _held is { } held && DateTime.UtcNow - held.MintedUtc < GoodFor ? held : null;

    /// <summary>
    /// Takes a token a television has minted.
    /// </summary>
    /// <param name="visitorData">The visitor id it was minted against.</param>
    /// <param name="streamToken">The token for stream URLs.</param>
    /// <param name="playerToken">The token for the player request, when there is a separate one.</param>
    /// <returns>What is now held.</returns>
    public static Minted Take(string visitorData, string streamToken, string? playerToken)
    {
        var minted = new Minted(
            visitorData.Trim(),
            streamToken.Trim(),
            string.IsNullOrWhiteSpace(playerToken) ? null : playerToken.Trim(),
            DateTime.UtcNow);

        _held = minted;
        return minted;
    }

    /// <summary>Forgets what is held. For testing, and for a television that knows its token is bad.</summary>
    public static void Forget() => _held = null;

    /// <summary>
    /// Puts the token on a stream URL, which is where the sixty-second wall is enforced.
    /// </summary>
    /// <param name="url">The stream address as YouTube gave it.</param>
    /// <returns>The address to hand a player.</returns>
    public static string Sign(string url)
    {
        // Only Google's own stream hosts. The ladder still has mirrors behind it, and hanging
        // a Google token on somebody else's address would be noise at best.
        if (Held is not { } held
            || string.IsNullOrEmpty(url)
            || url.Contains("pot=", StringComparison.Ordinal)
            || !Uri.TryCreate(url, UriKind.Absolute, out var address)
            || !address.Host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}pot={Uri.EscapeDataString(held.StreamToken)}");
    }
}
