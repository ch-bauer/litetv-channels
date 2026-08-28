using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiteTv.Trailers;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// Which language YouTube is asked in - which is what a YouTube programme is CALLED in the
/// schedule.
/// <para>
/// The fault this replaces: every call said <c>hl=en, gl=US</c>, written into two files, so a
/// German household read an English schedule for videos that had German titles. YouTube
/// localises a title and falls back to the original where there is no translation, so <c>hl</c>
/// is already "German first" - nothing is lost by asking in German.
/// </para>
/// </summary>
public class YouTubeLocaleTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void TheSettingWinsOverEverything()
    {
        Assert.Equal(("de", "DE"), YouTubeLocale.From("de-DE", "en", Invariant));
    }

    /// <summary>
    /// Somebody reading the plugin in German wants German titles and should not have to say so
    /// twice. This is what makes the fix arrive without anybody configuring anything.
    /// </summary>
    [Fact]
    public void ThePagesLanguageIsUsedWhenTheSettingIsEmpty()
    {
        Assert.Equal(("de", "DE"), YouTubeLocale.From(null, "de", Invariant));
        Assert.Equal(("de", "DE"), YouTubeLocale.From(string.Empty, "de", Invariant));
    }

    /// <summary>"auto" names no language, so it must not be sent as one.</summary>
    [Fact]
    public void AutoIsNotALanguage()
    {
        Assert.Equal(("en", "US"), YouTubeLocale.From(null, "auto", Invariant));
        Assert.Equal(("fr", "FR"), YouTubeLocale.From(null, "auto", new CultureInfo("fr-FR")));
    }

    [Fact]
    public void TheServersCultureIsTheLastRealAnswer()
    {
        Assert.Equal(("de", "AT"), YouTubeLocale.From(null, null, new CultureInfo("de-AT")));
    }

    /// <summary>An invariant culture names nothing, and must not become <c>hl=iv</c>.</summary>
    [Fact]
    public void AnInvariantCultureFallsBackRatherThanBeingSent()
    {
        Assert.Equal(("en", "US"), YouTubeLocale.From(null, null, Invariant));
    }

    [Theory]
    [InlineData("de", "de", "DE")]
    [InlineData("de-DE", "de", "DE")]
    [InlineData("de_AT", "de", "AT")]
    [InlineData("  en-GB  ", "en", "GB")]
    [InlineData("pt-br", "pt", "BR")]
    public void TagsAreSplitIntoTheTwoHalvesYouTubeWants(string tag, string language, string region)
    {
        Assert.Equal((language, region), YouTubeLocale.From(tag, null, Invariant));
    }

    /// <summary>
    /// <b>No caller may hard-code the language again.</b> That is what the fault was: two files
    /// each saying <c>en</c>/<c>US</c> in a plugin nobody German could read the schedule of. The
    /// source itself is checked, because a rule that lives only in a reviewer's head is one that
    /// comes back.
    /// </summary>
    [Fact]
    public void NothingAsksYouTubeInAHardCodedLanguage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.LiteTv.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var source = Path.Combine(dir!.FullName, "src", "Jellyfin.Plugin.LiteTv");

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            // The resolver of the rule itself is allowed to name a fallback.
            if (Path.GetFileName(file) == "YouTubeLocale.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var offenders = Regex.Matches(text, @"\[""(hl|gl)""\]\s*=\s*""[^""]+""");
            Assert.True(
                offenders.Count == 0,
                Path.GetFileName(file) + " asks YouTube in a hard-coded language: "
                    + string.Join(", ", offenders.Select(m => m.Value))
                    + ". Use YouTubeLocale.");
        }
    }
}
