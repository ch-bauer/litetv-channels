using Jellyfin.Plugin.LiteTv.Trailers;
using Jellyfin.Plugin.LiteTv.Api;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The token the television mints. What is worth testing here is not the token - that comes
/// from Google - but the two ways of getting it wrong that would fail silently: signing an
/// address that is not Google's, and signing one twice.
/// </summary>
[Collection("ProofOfOrigin")]
public class ProofOfOriginTests : IDisposable
{
    private const string Stream = "https://rr1---sn-abc.googlevideo.com/videoplayback?expire=1&ei=2";

    public ProofOfOriginTests() => ProofOfOrigin.Forget();

    public void Dispose()
    {
        ProofOfOrigin.Forget();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void HoldsNothingUntilATelevisionMintsSomething()
    {
        Assert.Null(ProofOfOrigin.Held);
        Assert.Equal(Stream, ProofOfOrigin.Sign(Stream));
    }

    [Fact]
    public void PutsTheTokenOnAStreamAddress()
    {
        ProofOfOrigin.Take("visitor-1", "token-1", null);

        Assert.Equal(Stream + "&pot=token-1", ProofOfOrigin.Sign(Stream));
    }

    /// <summary>
    /// A URL with no query of its own still has to end up with a well-formed one.
    /// </summary>
    [Fact]
    public void StartsAQueryWhenThereIsNone()
    {
        ProofOfOrigin.Take("visitor-1", "token-1", null);

        Assert.Equal(
            "https://rr1---sn-abc.googlevideo.com/videoplayback?pot=token-1",
            ProofOfOrigin.Sign("https://rr1---sn-abc.googlevideo.com/videoplayback"));
    }

    /// <summary>
    /// The ladder still has Piped and Invidious behind it. Hanging a Google token on somebody
    /// else's address is noise, and on a mirror that proxies the stream it is a token handed to
    /// a stranger.
    /// </summary>
    [Theory]
    [InlineData("https://pipedapi.kavin.rocks/streams/abc")]
    [InlineData("https://invidious.fdn.fr/api/v1/videos/abc")]
    [InlineData("https://example.com/googlevideo.com/videoplayback")]
    public void LeavesEverybodyElsesAddressesAlone(string url)
    {
        ProofOfOrigin.Take("visitor-1", "token-1", null);

        Assert.Equal(url, ProofOfOrigin.Sign(url));
    }

    /// <summary>
    /// Signing runs where the address comes out of the answer, and an address can pass through
    /// more than one hand before it is played. Twice must not mean two tokens.
    /// </summary>
    [Fact]
    public void NeverSignsTheSameAddressTwice()
    {
        ProofOfOrigin.Take("visitor-1", "token-1", null);

        var once = ProofOfOrigin.Sign(Stream);

        Assert.Equal(once, ProofOfOrigin.Sign(once));
    }

    [Fact]
    public void KeepsTheVisitorItWasMintedAgainst()
    {
        ProofOfOrigin.Take(" visitor-1 ", " token-1 ", "  ");

        var held = ProofOfOrigin.Held;

        Assert.NotNull(held);
        Assert.Equal("visitor-1", held.VisitorData);
        Assert.Equal("token-1", held.StreamToken);

        // Whitespace is not a player token. Sending one where none was minted is the failure
        // this guards: a token from the wrong context is refused, and looks like no token.
        Assert.Null(held.PlayerToken);
    }

    [Fact]
    public void TakesTheNewestToken()
    {
        ProofOfOrigin.Take("visitor-1", "token-1", null);
        ProofOfOrigin.Take("visitor-2", "token-2", "player-2");

        Assert.Equal("visitor-2", ProofOfOrigin.Held?.VisitorData);
        Assert.Equal("player-2", ProofOfOrigin.Held?.PlayerToken);
    }

    /// <summary>
    /// A token does not merely improve the next resolution - it makes the last one wrong. A
    /// trailer resolved seconds before a television minted is a capped stream, and anything
    /// caching it has to be able to tell that it is stale.
    /// </summary>
    [Fact]
    public void MovesOnAGenerationWheneverTheHeldTokenChanges()
    {
        var start = ProofOfOrigin.Generation;

        ProofOfOrigin.Take("visitor-1", "token-1", null);
        var afterFirst = ProofOfOrigin.Generation;

        ProofOfOrigin.Take("visitor-2", "token-2", null);
        var afterSecond = ProofOfOrigin.Generation;

        ProofOfOrigin.Forget();

        Assert.NotEqual(start, afterFirst);
        Assert.NotEqual(afterFirst, afterSecond);
        Assert.NotEqual(afterSecond, ProofOfOrigin.Generation);
    }

    [Fact]
    public void RejectsExpiredAndFutureTokenTimestamps()
    {
        var now = DateTime.UtcNow;
        var expired = new ProofOfOrigin.Minted("visitor", "token", null, now.AddHours(-6));
        var future = new ProofOfOrigin.Minted("visitor", "token", null, now.AddMinutes(1));

        Assert.False(ProofOfOrigin.IsUsable(expired, now));
        Assert.False(ProofOfOrigin.IsUsable(future, now));
        Assert.True(ProofOfOrigin.IsUsable(expired with { MintedUtc = now.AddMinutes(-5) }, now));
    }

    [Fact]
    public void DoesNotPresentManifestSentinelAsPixelQuality()
    {
        var words = LiteTvController.LastResolvedWords(
            new YouTubeStreamResolver.Resolution("video", int.MaxValue, "VISIONOS", true, DateTime.UtcNow));

        Assert.Equal("unknown quality · VISIONOS · with a token", words);
    }

    [Fact]
    public void HasNoWordsForMissingResolution()
        => Assert.Null(LiteTvController.LastResolvedWords(null));
}
