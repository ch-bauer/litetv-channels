using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiteTv.Updates;

/// <summary>
/// What the update server knows about one stored APK, read out of its file name.
/// <para>
/// A build carries no metadata anybody here can read - an APK is a zip and the version is
/// inside a binary manifest - but Gradle already writes everything worth knowing into the file
/// name: <c>Wholphin-default-release-1.0.5-22-g7b77227d-57-armeabi-v7a.apk</c>. Reading the
/// name is exact for any build this fork produced and forgiving for anything else.
/// </para>
/// </summary>
internal sealed record AppBuild(
    string FileName,
    string Path,
    BuildVersion Version,
    string BuildType,
    string? Abi,
    long Bytes,
    DateTime Modified)
{
    /// <summary>The names the app asks for, in the order it asks. Kept from UpdateChecker.kt.</summary>
    private const string AssetName = "Wholphin";

    /// <summary>
    /// The build APKs, as Gradle names them. The version is what <c>git describe</c> produced,
    /// so it carries dashes of its own and has to be matched rather than split on.
    /// </summary>
    private static readonly Regex GradleName = new(
        @"^Wholphin-(?<flavour>[A-Za-z0-9]+)-(?<type>release|debug)-(?<version>\d+\.\d+\.\d+(?:-\d+-g[0-9a-fA-F]+)?)-(?<code>\d+)(?:-(?<abi>[A-Za-z0-9_]+(?:-[A-Za-z0-9_]+)*))?\.apk$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// The version on its own, for a file somebody renamed. A build whose name no longer says
    /// what it is should still be servable - the alternative is an upload that is accepted and
    /// then silently never offered.
    /// </summary>
    private static readonly Regex LooseVersion = new(
        @"(?<version>\d+\.\d+\.\d+(?:-\d+-g[0-9a-fA-F]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>The ABIs a build can be for, longest first so the match is unambiguous.</summary>
    private static readonly string[] KnownAbis = { "armeabi-v7a", "arm64-v8a", "x86_64", "x86" };

    /// <summary>Gets a value indicating whether this is a release build rather than a debug one.</summary>
    public bool IsRelease => string.Equals(BuildType, "release", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads what a file name says about a build, or answers null when it says nothing.
    /// </summary>
    /// <param name="fileName">The file's name.</param>
    /// <param name="bytes">Its size.</param>
    /// <param name="modified">When it was written.</param>
    /// <param name="path">Where it is.</param>
    /// <returns>The build, or null when no version could be read.</returns>
    public static AppBuild? Describe(string fileName, long bytes, DateTime modified, string path)
    {
        var gradle = GradleName.Match(fileName);
        if (gradle.Success && BuildVersion.TryParse(gradle.Groups["version"].Value) is { } version)
        {
            return new AppBuild(
                fileName,
                path,
                version,
                gradle.Groups["type"].Value.ToLowerInvariant(),
                AbiOf(gradle.Groups["abi"].Success ? gradle.Groups["abi"].Value : null, fileName),
                bytes,
                modified);
        }

        var loose = LooseVersion.Match(fileName);
        if (!loose.Success || BuildVersion.TryParse(loose.Groups["version"].Value) is not { } parsed)
        {
            return null;
        }

        return new AppBuild(
            fileName,
            path,
            parsed,
            fileName.Contains("debug", StringComparison.OrdinalIgnoreCase) ? "debug" : "release",
            AbiOf(null, fileName),
            bytes,
            modified);
    }

    /// <summary>
    /// The ABI, and only if it is one.
    /// <para>
    /// Whatever the name says has to be checked against the ABIs that exist, because the ABI
    /// goes straight into an asset name and the app asks for asset names <em>exactly</em>: an
    /// ABI read wrong is an update that is never offered and never explained. A name can be
    /// wrong in the middle and still match - a hash that is not hex leaves the version's commit
    /// part unmatched, the build number takes its place, and everything after it looks like an
    /// ABI. When it is not one, the file is treated as universal, which is the honest reading of
    /// "the name does not say".
    /// </para>
    /// </summary>
    private static string? AbiOf(string? candidate, string fileName)
    {
        if (candidate is not null)
        {
            var exact = KnownAbis.FirstOrDefault(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return KnownAbis.FirstOrDefault(a => fileName.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The builds that make up the release on offer: the newest version, and every file of that
    /// same version beside it.
    /// <para>
    /// Release builds decide which version is on offer, because the television is what this
    /// exists for. A debug build is only ever offered when there is no release build at all -
    /// naming a version whose asset the app cannot find would have it announce an update and
    /// then fail to fetch one, which is worse than saying nothing.
    /// </para>
    /// </summary>
    /// <param name="builds">Everything the store holds.</param>
    /// <returns>The builds of the version on offer, or an empty list.</returns>
    public static IReadOnlyList<AppBuild> Newest(IReadOnlyCollection<AppBuild> builds)
    {
        if (builds.Count == 0)
        {
            return Array.Empty<AppBuild>();
        }

        var kind = builds.Any(b => b.IsRelease) ? "release" : "debug";
        var ofKind = builds
            .Where(b => string.Equals(b.BuildType, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.Version)
            .ToList();

        var version = ofKind[0].Version;
        return ofKind.Where(b => b.Version.CompareTo(version) == 0).ToList();
    }

    /// <summary>
    /// Which stored build answers to which asset name.
    /// <para>
    /// The app asks for one exact name, then the next, then the next, and takes the first that
    /// exists - so an asset list is a set of names rather than a set of files, and one file can
    /// answer to several. The bare names are answered by the build most likely to run anywhere:
    /// a build with no ABI in its name is universal, and a 32-bit build runs on a 64-bit device
    /// while the reverse does not.
    /// </para>
    /// </summary>
    /// <param name="builds">The builds of the version on offer.</param>
    /// <returns>Asset name to build, in the order they should be advertised.</returns>
    public static IReadOnlyList<KeyValuePair<string, AppBuild>> Assets(IReadOnlyCollection<AppBuild> builds)
    {
        var assets = new List<KeyValuePair<string, AppBuild>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, AppBuild build)
        {
            if (seen.Add(name))
            {
                assets.Add(new KeyValuePair<string, AppBuild>(name, build));
            }
        }

        foreach (var build in builds.Where(b => b.Abi is not null))
        {
            Add($"{AssetName}-{build.BuildType}-{build.Abi}.apk", build);
        }

        if (builds.Count == 0)
        {
            return assets;
        }

        var fallback = builds.FirstOrDefault(b => b.Abi is null)
            ?? builds.FirstOrDefault(b => string.Equals(b.Abi, "armeabi-v7a", StringComparison.OrdinalIgnoreCase))
            ?? builds.First();

        Add($"{AssetName}-{fallback.BuildType}.apk", fallback);
        if (fallback.IsRelease)
        {
            Add($"{AssetName}.apk", fallback);
        }

        return assets;
    }
}

/// <summary>
/// A version as <c>git describe</c> writes it, ordered the way the app orders them.
/// <para>
/// The app compares major, then minor, then patch, then the number of commits since the tag -
/// so two builds off the same tag are ordered by their commit count, which is the only thing
/// that ever moves between two builds of this fork. The hash is carried because the app's own
/// version string contains it and the two have to be written the same way.
/// </para>
/// </summary>
internal sealed record BuildVersion(int Major, int Minor, int Patch, int Commits, string? Hash)
    : IComparable<BuildVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:-(\d+)-g([0-9a-fA-F]+))?$",
        RegexOptions.CultureInvariant);

    /// <summary>Reads a version, or answers null.</summary>
    /// <param name="text">The version string.</param>
    /// <returns>The version, or null when it is not one.</returns>
    public static BuildVersion? TryParse(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new BuildVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0,
            match.Groups[5].Success ? match.Groups[5].Value : null);
    }

    /// <inheritdoc />
    public int CompareTo(BuildVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : Commits.CompareTo(other.Commits);
    }

    /// <summary>Written the way the app parses it: <c>v1.0.5-22-g7b77227d</c>.</summary>
    /// <returns>The version string.</returns>
    public override string ToString() =>
        Commits > 0 && Hash is not null
            ? string.Create(CultureInfo.InvariantCulture, $"v{Major}.{Minor}.{Patch}-{Commits}-g{Hash}")
            : string.Create(CultureInfo.InvariantCulture, $"v{Major}.{Minor}.{Patch}");
}
