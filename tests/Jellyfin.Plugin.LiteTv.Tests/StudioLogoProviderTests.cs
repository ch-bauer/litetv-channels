using System.Net;
using Jellyfin.Plugin.LiteTv.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The online fallback for a studio channel suggestion's own picture: TMDb's company logo,
/// asked for only when the owner gave a key and only when the library has nothing of its own
/// - see <see cref="Api.LiteTvController"/>'s <c>StudioArtworkAsync</c> for where this sits in
/// that order.
/// </summary>
public class StudioLogoProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static (StudioLogoProvider Provider, StubHandler Handler) Provider(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json)
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        factory.CreateClient().Returns(client);
        return (new StudioLogoProvider(factory, NullLogger<StudioLogoProvider>.Instance), handler);
    }

    /// <summary>No key, no request at all - the whole point of the setting being optional.</summary>
    [Fact]
    public async Task NoApiKeyMeansNoLookup()
    {
        var (provider, handler) = Provider("{\"results\":[]}");

        var logo = await provider.FindLogoAsync(string.Empty, "DreamWorks", CancellationToken.None);

        Assert.Null(logo);
        Assert.Null(handler.LastRequest);
    }

    /// <summary>The first result carrying a logo wins; TMDb's own search ranking is trusted.</summary>
    [Fact]
    public async Task TheFirstResultWithALogoIsReturned()
    {
        var (provider, _) = Provider("""
            {"results":[
                {"id":1,"name":"DreamWorks (unrelated)"},
                {"id":2,"name":"DreamWorks Animation","logo_path":"/abc123.png"}
            ]}
            """);

        var logo = await provider.FindLogoAsync("test-key", "DreamWorks", CancellationToken.None);

        Assert.Equal("https://image.tmdb.org/t/p/w500/abc123.png", logo);
    }

    /// <summary>No match at all is a null, not an exception - a logo is decoration.</summary>
    [Fact]
    public async Task NoMatchingCompanyIsNullRatherThanAFailure()
    {
        var (provider, _) = Provider("{\"results\":[]}");

        var logo = await provider.FindLogoAsync("test-key", "Nobody Studios", CancellationToken.None);

        Assert.Null(logo);
    }

    /// <summary>A server error is swallowed the same way: the suggestion falls back, it does not fail.</summary>
    [Fact]
    public async Task AFailedRequestIsNullRatherThanThrown()
    {
        var (provider, _) = Provider("oops", HttpStatusCode.InternalServerError);

        var logo = await provider.FindLogoAsync("test-key", "DreamWorks", CancellationToken.None);

        Assert.Null(logo);
    }
}
