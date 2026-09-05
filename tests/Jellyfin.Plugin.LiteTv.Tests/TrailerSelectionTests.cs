using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class TrailerSelectionTests
{
    [Fact]
    public void ConfiguredLanguageBeatsAnExplicitMismatch()
    {
        Assert.Equal(0, TrailerSelection.LanguageRank("Film - Offizieller Trailer Deutsch", "de"));
        Assert.Equal(2, TrailerSelection.LanguageRank("Film - Official Trailer English", "de"));
    }

    [Fact]
    public void UnlabelledTrailerIsAValidFallback()
    {
        Assert.Equal(1, TrailerSelection.LanguageRank("Film - Official Trailer", "de"));
    }

    [Fact]
    public void FullTrailerBeatsATeaserWhenEverythingElseTies()
    {
        Assert.True(
            TrailerSelection.KindRank("Official Trailer") < TrailerSelection.KindRank("Teaser"));
    }
}
