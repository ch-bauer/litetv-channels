using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The signature a signed-in YouTube session sends. It is worth testing precisely because it
/// cannot be tested against YouTube: a wrong signature comes back as a plain refusal that looks
/// exactly like the bot gate, so the only way to know the header is right is to compute it
/// twice by different routes.
/// </summary>
public class YouTubeAccountTests
{
    private const string Sapisid = "abcdef1234567890";

    private static readonly DateTimeOffset Moment = DateTimeOffset.FromUnixTimeSeconds(1_777_000_000);

    [Theory]
    [InlineData("SAPISID=abcdef1234567890")]
    [InlineData("__Secure-3PAPISID=abcdef1234567890")]
    [InlineData("YSC=x; SAPISID=abcdef1234567890; VISITOR_INFO1_LIVE=y")]
    [InlineData("  YSC = x ;  __Secure-3PAPISID = abcdef1234567890 ; ")]
    public void FindsTheSessionIdInAPastedCookie(string cookie) =>
        Assert.Equal(Sapisid, YouTubeStreamResolver.Sapisid(cookie));

    /// <summary>
    /// The third-party id is the one the web player signs with, so it wins when a paste has
    /// both - which every real paste from a browser does.
    /// </summary>
    [Fact]
    public void PrefersTheThirdPartySessionId() =>
        Assert.Equal(
            "third",
            YouTubeStreamResolver.Sapisid("SAPISID=first; __Secure-3PAPISID=third"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YSC=x; VISITOR_INFO1_LIVE=y")]
    [InlineData("SAPISID=")]
    public void SaysNothingWhenThereIsNoSession(string? cookie)
    {
        Assert.Null(YouTubeStreamResolver.Sapisid(cookie));
        Assert.Null(YouTubeStreamResolver.AuthorizationFor(cookie, Moment));
    }

    /// <summary>
    /// Computed here the long way round, from the scheme's own description, so that the header
    /// is checked against the specification rather than against itself.
    /// </summary>
    [Fact]
    public void SignsTheWayTheWebPlayerDoes()
    {
        var expected = Convert
            .ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                "1777000000 abcdef1234567890 https://www.youtube.com")))
            .ToLowerInvariant();

        Assert.Equal(
            $"SAPISIDHASH 1777000000_{expected}",
            YouTubeStreamResolver.AuthorizationFor("SAPISID=" + Sapisid, Moment));
    }

    /// <summary>
    /// The seconds are in the signature, so two requests a minute apart must not carry the same
    /// header - that is the whole of the replay window.
    /// </summary>
    [Fact]
    public void StampsEachRequestWithItsOwnMoment() =>
        Assert.NotEqual(
            YouTubeStreamResolver.AuthorizationFor("SAPISID=" + Sapisid, Moment),
            YouTubeStreamResolver.AuthorizationFor("SAPISID=" + Sapisid, Moment.AddMinutes(1)));

    /// <summary>
    /// The fault that made the whole thing fail silently the first time it was tried on a real
    /// server: a browser's cookie header is enormous - 166 cookies and 182 kilobytes, measured
    /// on 22 Aug 2026 - and a header that size is refused before anything reads it. Trimmed to
    /// the session it is under two kilobytes and the same request succeeds.
    /// </summary>
    [Fact]
    public void KeepsOnlyTheSessionOutOfAWholeBrowserPaste()
    {
        var pasted = "YSC=y; ST-1abc=junk; SAPISID=" + Sapisid
            + "; ST-2def=more junk; __Secure-3PAPISID=third; NOISE=x; LOGIN_INFO=li";

        var sent = YouTubeStreamResolver.AccountCookie(pasted);

        Assert.Equal("YSC=y; SAPISID=abcdef1234567890; __Secure-3PAPISID=third; LOGIN_INFO=li", sent);
        Assert.DoesNotContain("ST-", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("NOISE", sent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A youtube.com-only export is the shape that looks right and is not: the id the signature
    /// needs is a google.com cookie. Nothing is sent, and the resolver says so in the log.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("YSC=y; VISITOR_INFO1_LIVE=v; ST-1abc=junk")]
    public void SendsNothingWhenThePasteCarriesNoSession(string? cookie) =>
        Assert.Null(YouTubeStreamResolver.AccountCookie(cookie));

    /// <summary>
    /// The signature is computed from the paste, not from the trimmed header, so trimming must
    /// not change it.
    /// </summary>
    [Fact]
    public void TrimmingDoesNotChangeTheSignature()
    {
        var pasted = "ST-1abc=junk; SAPISID=" + Sapisid + "; NOISE=x";

        Assert.Equal(
            YouTubeStreamResolver.AuthorizationFor("SAPISID=" + Sapisid, Moment),
            YouTubeStreamResolver.AuthorizationFor(pasted, Moment));
    }
}
