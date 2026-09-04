using Jellyfin.Plugin.LiteTv.Api;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A studio channel's face should be the studio's own mark - a logo, when the library has one -
/// rather than whichever title happened to rank first in its lineup.
/// <para>
/// The lookup itself needs a library manager and is exercised through the controller; what is
/// tested here is the rule that decides which studio name to ask for, and that a studio with no
/// picture never wins over one that has one.
/// </para>
/// </summary>
public class StudioArtworkResolverTests
{
    private static Func<string, (Guid Id, string Name)?> Library(params (string Name, Guid Id)[] pictured)
    {
        var byName = pictured.ToDictionary(entry => entry.Name, entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        return name => byName.TryGetValue(name, out var id) ? (id, name) : null;
    }

    [Fact]
    public void TheMostCommonStudioIsTriedFirst()
    {
        var dreamWorks = Guid.NewGuid();
        var studiosPerSource = new[]
        {
            new[] { "DreamWorks Pictures" },
            new[] { "DreamWorks Animation" },
            new[] { "DreamWorks Animation" },
            new[] { "DreamWorks Animation" },
        };

        var result = StudioArtworkResolver.Resolve(
            studiosPerSource,
            Library(("DreamWorks Animation", dreamWorks), ("DreamWorks Pictures", Guid.NewGuid())));

        Assert.Equal(dreamWorks, result!.ItemId);
        Assert.Equal("DreamWorks Animation", result.ItemName);
    }

    /// <summary>
    /// The report this answers: a caper carried "DreamWorks Pictures" and animated films carried
    /// "DreamWorks Animation". Only the library knows which of those two strings, if either, was
    /// ever given a picture - the loose match term "dreamworks" that found the titles names
    /// neither.
    /// </summary>
    [Fact]
    public void ASourceWithNoPictureIsSkippedForOneThatHasOne()
    {
        var animation = Guid.NewGuid();
        var studiosPerSource = new[]
        {
            new[] { "DreamWorks Animation" },
            new[] { "DreamWorks Animation" },
            new[] { "DreamWorks Pictures" },
            new[] { "DreamWorks Pictures" },
            new[] { "DreamWorks Pictures" },
        };

        // Pictures is the majority here, but only Animation has a picture in the library.
        var result = StudioArtworkResolver.Resolve(
            studiosPerSource,
            Library(("DreamWorks Animation", animation)));

        Assert.Equal(animation, result!.ItemId);
    }

    [Fact]
    public void NoStudioWithAPictureMeansNoOverride()
    {
        var studiosPerSource = new[] { new[] { "DreamWorks Animation" }, new[] { "Marvel Studios" } };

        var result = StudioArtworkResolver.Resolve(studiosPerSource, Library());

        Assert.Null(result);
    }

    [Fact]
    public void TitlesWithNoStudioMetadataContributeNothing()
    {
        var marvel = Guid.NewGuid();
        var studiosPerSource = new[]
        {
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { "Marvel Studios" },
        };

        var result = StudioArtworkResolver.Resolve(studiosPerSource, Library(("Marvel Studios", marvel)));

        Assert.Equal(marvel, result!.ItemId);
    }

    [Fact]
    public void AnEmptyPoolResolvesToNothing()
    {
        Assert.Null(StudioArtworkResolver.Resolve(Array.Empty<IReadOnlyList<string>>(), Library()));
    }
}
