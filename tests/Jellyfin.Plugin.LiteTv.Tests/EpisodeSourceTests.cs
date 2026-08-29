using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.LiteTv.Configuration;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// A single episode can be a channel's content.
/// <para>
/// The owner asked for one search bar per screen that finds "films, series, episodes,
/// collections and links". Four of those five already round-tripped; an episode had no name in
/// <see cref="ChannelSourceType"/> at all, so the page had no honest way to say what it had put
/// on the list.
/// </para>
/// <para>
/// Scheduling needed nothing: <c>ChannelPlaylistBuilder.Expand</c> switches on the library
/// <b>item</b>, so an episode falls to the same branch a film does and becomes one entry. What
/// is tested here is the part that could actually break - that the new value survives being
/// written and read back, and that a file carrying it does not cost the channel.
/// </para>
/// </summary>
public class EpisodeSourceTests : IDisposable
{
    private readonly string _root;

    public EpisodeSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "litetv-episodesource-" + Guid.NewGuid().ToString("N"));
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
        }
    }

    private ChannelStore Store()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_root);
        return new ChannelStore(paths, NullLogger<ChannelStore>.Instance);
    }

    [Fact]
    public void AnEpisodeSourceSurvivesBeingWrittenAndReadBack()
    {
        var episode = Guid.NewGuid();
        var channel = new TvChannel
        {
            Id = Guid.NewGuid(),
            Name = "One episode",
            Sources = new List<ChannelSource>
            {
                new() { Type = ChannelSourceType.Episode, ItemId = episode, Name = "Folge 3" }
            }
        };

        Store().Save(channel);
        var read = Store().Get(channel.Id);

        Assert.NotNull(read);
        var source = Assert.Single(read!.Sources);
        Assert.Equal(ChannelSourceType.Episode, source.Type);
        Assert.Equal(episode, source.ItemId);
    }

    /// <summary>
    /// The value the page writes is the name, not the number. A source written as
    /// <c>"Episode"</c> has to read back as <see cref="ChannelSourceType.Episode"/> - this is
    /// exactly the shape of thing that made every channel unsaveable once, when a page wrote a
    /// name the server had never heard of.
    /// </summary>
    [Fact]
    public void TheNameThePageWritesIsTheNameTheServerReads()
    {
        var id = Guid.NewGuid();
        var json = "{\"Id\":\"" + id.ToString("D") + "\",\"Name\":\"By name\",\"Sources\":"
            + "[{\"Type\":\"Episode\",\"ItemId\":\"" + Guid.NewGuid().ToString("D") + "\",\"Name\":\"Folge 4\"}]}";

        var folder = Path.Combine(_root, "LiteTv", "channels");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id.ToString("N") + ".json"), json);

        var read = Store().Get(id);

        Assert.NotNull(read);
        Assert.Equal(ChannelSourceType.Episode, Assert.Single(read!.Sources).Type);
    }

    /// <summary>
    /// And the number it might be written as, since an enum on the wire has been both.
    /// </summary>
    [Fact]
    public void TheNumberFourIsAnEpisodeToo()
    {
        Assert.Equal(ChannelSourceType.Episode, (ChannelSourceType)4);
        Assert.Equal(
            ChannelSourceType.Episode,
            JsonSerializer.Deserialize<ChannelSourceType>("4"));
    }
}
