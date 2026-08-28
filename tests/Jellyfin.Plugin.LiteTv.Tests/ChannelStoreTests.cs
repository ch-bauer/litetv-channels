using System;
using System.IO;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The channels, a file each.
/// <para>
/// This class exists because of what the old shape cost. The channels were a list inside the
/// plugin configuration, and a configuration is saved as <b>one document</b>: a channel written
/// with a <see cref="TrailerMode"/> that did not exist made the whole document unreadable, the
/// server answered 500, and <b>nothing could be saved</b> - not the new channel, and not the
/// four valid ones beside it. Creating a channel bricked the plugin.
/// </para>
/// <para>
/// So the test that matters here is not the round trip. It is
/// <see cref="OneUnreadableChannelLeavesEveryOtherChannelOnTheAir"/>: a broken file must cost
/// exactly one channel. Written to fail against the old shape - a list in one document has no
/// way to pass it.
/// </para>
/// </summary>
public class ChannelStoreTests : IDisposable
{
    private readonly string _root;

    public ChannelStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "litetv-channelstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }

    private ChannelStore Store()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_root);
        return new ChannelStore(paths, NullLogger<ChannelStore>.Instance);
    }

    private string ChannelFolder => Path.Combine(_root, "LiteTv", "channels");

    private static TvChannel Channel(string name)
        => new() { Id = Guid.NewGuid(), Name = name, Enabled = true };

    [Fact]
    public void AChannelSurvivesTheRoundTrip()
    {
        var written = Channel("Comedy");
        written.Trailers = TrailerMode.Preview;
        written.Order = PlayOrder.Shuffle;
        Store().Save(written);

        var read = Store().Get(written.Id);

        Assert.NotNull(read);
        Assert.Equal("Comedy", read!.Name);
        Assert.Equal(TrailerMode.Preview, read.Trailers);
        Assert.Equal(PlayOrder.Shuffle, read.Order);
    }

    /// <summary>
    /// Enums are written as names, so a channel file can be read and repaired by hand - and so
    /// an unknown one fails loudly instead of arriving as a number that means something else.
    /// </summary>
    [Fact]
    public void EnumsAreWrittenAsNames()
    {
        var channel = Channel("Docs");
        channel.Trailers = TrailerMode.Preview;
        Store().Save(channel);

        var onDisk = File.ReadAllText(Directory.GetFiles(ChannelFolder, "*.json")[0]);

        Assert.Contains("\"Preview\"", onDisk, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The reason this store exists.</b> One channel the server cannot read costs one
    /// channel. The old shape - every channel in one document - could only ever fail all of
    /// them together, which is exactly what happened.
    /// </summary>
    [Fact]
    public void OneUnreadableChannelLeavesEveryOtherChannelOnTheAir()
    {
        var good = Channel("Good");
        var alsoGood = Channel("Also good");
        var store = Store();
        store.Save(good);
        store.Save(alsoGood);

        // The 'Between' that was never a TrailerMode, in a file of its own this time.
        File.WriteAllText(
            Path.Combine(ChannelFolder, Guid.NewGuid().ToString("N") + ".json"),
            "{ \"Id\": \"" + Guid.NewGuid() + "\", \"Name\": \"Broken\", \"Trailers\": \"Between\" }");

        var read = Store().All();

        Assert.Equal(2, read.Count);
        Assert.Contains(read, c => c.Id == good.Id);
        Assert.Contains(read, c => c.Id == alsoGood.Id);
    }

    /// <summary>
    /// Saving one channel writes one file, so a page that has never heard of a channel somebody
    /// else just made cannot delete it by leaving it out. The old page posted the whole list.
    /// </summary>
    [Fact]
    public void SavingOneChannelLeavesTheOthersWhereTheyWere()
    {
        var kept = Channel("Kept");
        var store = Store();
        store.Save(kept);

        var later = Store();
        later.Save(Channel("New"));

        Assert.NotNull(Store().Get(kept.Id));
    }

    /// <summary>A folder has no order of its own, so the position is written down.</summary>
    [Fact]
    public void ANewChannelGoesToTheEnd()
    {
        var store = Store();
        var first = Channel("First");
        var second = Channel("Second");
        store.Save(first);
        store.Save(second);

        var read = Store().All();

        Assert.Equal(new[] { first.Id, second.Id }, new[] { read[0].Id, read[1].Id });
    }

    [Fact]
    public void DeletingAChannelTakesItsFileWithIt()
    {
        var channel = Channel("Gone");
        var store = Store();
        store.Save(channel);

        Assert.True(store.Delete(channel.Id));
        Assert.Null(Store().Get(channel.Id));
        Assert.False(store.Delete(channel.Id));
    }
}
