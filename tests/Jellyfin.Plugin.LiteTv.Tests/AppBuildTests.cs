using Jellyfin.Plugin.LiteTv.Updates;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The app picks an update by name and nothing else: it matches the version string whole, and
/// asks for assets by exact file name. Everything here is a rule taken from the app's own
/// <c>UpdateChecker.kt</c>, and every one of them fails silently - an update that is simply
/// never offered, with nothing in any log to say why.
/// </summary>
public class AppBuildTests
{
    private static readonly DateTime When = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static AppBuild Build(string name) =>
        AppBuild.Describe(name, 42, When, "/tmp/" + name)
        ?? throw new InvalidOperationException("expected " + name + " to read as a build");

    [Fact]
    public void ReadsAGradleName()
    {
        var build = Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk");

        Assert.Equal("release", build.BuildType);
        Assert.Equal("armeabi-v7a", build.Abi);
        Assert.Equal("v1.0.5-22-g7b77227d", build.Version.ToString());
    }

    [Fact]
    public void ReadsABuildWithNoAbi()
    {
        var build = Build("Wholphin-default-debug-1.0.5-22-g7b77227d-57.apk");

        Assert.Equal("debug", build.BuildType);
        Assert.Null(build.Abi);
    }

    /// <summary>A renamed file is still a build; refusing it would mean an upload that vanishes.</summary>
    [Fact]
    public void ReadsARenamedFile()
    {
        var build = Build("litetv-1.0.6-3-gabc1234-arm64-v8a.apk");

        Assert.Equal("release", build.BuildType);
        Assert.Equal("arm64-v8a", build.Abi);
        Assert.Equal("v1.0.6-3-gabc1234", build.Version.ToString());
    }

    /// <summary>
    /// Found by a smoke test against the live server: a hash that is not hex leaves the
    /// version's commit part unmatched, the build number slides into its place, and everything
    /// after it was taken for an ABI - which produced the asset name
    /// <c>Wholphin-release-gtest123-1-armeabi-v7a.apk</c>, a name the app never asks for.
    /// </summary>
    [Fact]
    public void NeverInventsAnAbi()
    {
        var build = Build("Wholphin-default-release-0.0.1-1-gtest123-1-armeabi-v7a.apk");

        Assert.Equal("armeabi-v7a", build.Abi);
        Assert.Contains("Wholphin-release-armeabi-v7a.apk", AppBuild.Assets(new[] { build }).Select(a => a.Key));
    }

    [Fact]
    public void TreatsAnUnknownAbiAsNone()
    {
        var build = Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-sparc.apk");

        Assert.Null(build.Abi);
        Assert.Equal(
            new[] { "Wholphin-release.apk", "Wholphin.apk" },
            AppBuild.Assets(new[] { build }).Select(a => a.Key));
    }

    [Fact]
    public void RefusesAFileWithNoVersion()
    {
        Assert.Null(AppBuild.Describe("Wholphin.apk", 42, When, "/tmp/Wholphin.apk"));
    }

    /// <summary>
    /// Two builds off the same tag differ only in their commit count, which is the comparison
    /// the app actually performs on this fork.
    /// </summary>
    [Fact]
    public void OrdersByCommitCountWithinAVersion()
    {
        var offered = AppBuild.Newest(new[]
        {
            Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk"),
            Build("Wholphin-default-release-1.0.5-9-gaaaaaaa-44-armeabi-v7a.apk")
        }, AppFamily.Wholphin);

        Assert.Single(offered);
        Assert.Equal("v1.0.5-22-g7b77227d", offered[0].Version.ToString());
    }

    [Fact]
    public void PrefersAReleaseBuildOverANewerDebugOne()
    {
        var offered = AppBuild.Newest(new[]
        {
            Build("Wholphin-default-debug-1.0.6-1-gbbbbbbb-58-armeabi-v7a.apk"),
            Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk")
        }, AppFamily.Wholphin);

        Assert.Single(offered);
        Assert.Equal("release", offered[0].BuildType);
    }

    /// <summary>The three names the app asks for, in the order it asks for them.</summary>
    [Fact]
    public void AdvertisesTheNamesTheAppAsksFor()
    {
        var assets = AppBuild.Assets(new[]
        {
            Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk")
        });

        Assert.Equal(
            new[] { "Wholphin-release-armeabi-v7a.apk", "Wholphin-release.apk", "Wholphin.apk" },
            assets.Select(a => a.Key));
    }

    /// <summary>
    /// Each ABI answers under its own name, and the bare names go to the 32-bit build - which
    /// runs on a 64-bit device, where the reverse does not.
    /// </summary>
    [Fact]
    public void GivesEachAbiItsOwnNameAndFallsBackToTheSafeOne()
    {
        var assets = AppBuild.Assets(new[]
        {
            Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-arm64-v8a.apk"),
            Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk")
        });

        Assert.Contains("Wholphin-release-arm64-v8a.apk", assets.Select(a => a.Key));
        Assert.Contains("Wholphin-release-armeabi-v7a.apk", assets.Select(a => a.Key));
        Assert.Equal(
            "Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk",
            assets.First(a => a.Key == "Wholphin.apk").Value.FileName);
    }

    /// <summary>A debug build is never offered under a release name, or the app fetches nothing.</summary>
    [Fact]
    public void NeverOffersADebugBuildUnderAReleaseName()
    {
        var assets = AppBuild.Assets(new[]
        {
            Build("Wholphin-default-debug-1.0.5-22-g7b77227d-57-armeabi-v7a.apk")
        });

        Assert.Equal(
            new[] { "Wholphin-debug-armeabi-v7a.apk", "Wholphin-debug.apk" },
            assets.Select(a => a.Key));
    }

    /// <summary>
    /// The version has to be written the way the app's regex reads it, or every check fails.
    /// </summary>
    [Theory]
    [InlineData("1.0.5-22-g7b77227d", "v1.0.5-22-g7b77227d")]
    [InlineData("v1.0.6", "v1.0.6")]
    [InlineData("1.0.6-0-gabc1234", "v1.0.6")]
    public void WritesVersionsTheWayTheAppReadsThem(string input, string expected)
    {
        Assert.Equal(expected, BuildVersion.TryParse(input)!.ToString());
    }

    /// <summary>findroid's own release naming - see its own `publish.yaml`.</summary>
    [Fact]
    public void ReadsAFindroidGradleName()
    {
        var build = Build("findroid-v1.2.0-libre-arm64-v8a.apk");

        Assert.Equal(AppFamily.Findroid, build.Family);
        Assert.Equal("release", build.BuildType);
        Assert.Equal("arm64-v8a", build.Abi);
        Assert.Equal("v1.2.0", build.Version.ToString());
    }

    /// <summary>findroid's client asks for its own exact file name - no synthesised alias.</summary>
    [Fact]
    public void AssetsForFindroidUsesTheLiteralFileName()
    {
        var build = Build("findroid-v1.2.0-libre-arm64-v8a.apk");

        var assets = AppBuild.AssetsForFindroid(new[] { build });

        Assert.Equal(
            new[] { "findroid-v1.2.0-libre-arm64-v8a.apk" },
            assets.Select(a => a.Key));
    }

    /// <summary>
    /// A build never counts toward the other app's "newest" - two forks sharing this store must
    /// never end up offering one another's release.
    /// </summary>
    [Fact]
    public void NewestNeverMixesFamilies()
    {
        var wholphin = Build("Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk");
        var findroid = Build("findroid-v9.9.9-libre-arm64-v8a.apk");

        var offeredWholphin = AppBuild.Newest(new[] { wholphin, findroid }, AppFamily.Wholphin);
        var offeredFindroid = AppBuild.Newest(new[] { wholphin, findroid }, AppFamily.Findroid);

        Assert.Equal(new[] { wholphin }, offeredWholphin);
        Assert.Equal(new[] { findroid }, offeredFindroid);
    }
}
