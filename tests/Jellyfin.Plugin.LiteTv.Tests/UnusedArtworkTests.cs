using System;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// An uploaded picture nobody points at any more is deleted.
/// <para>
/// This exists because of a file. Uploading writes one and hands the page an address; clearing
/// the slot, replacing the picture, or borrowing a title's artwork instead only ever changed
/// the address, so the file stayed on disk with nothing referring to it and no way to remove it
/// short of reaching the box. A crop test left a Spongebob banner sitting in the plugin folder
/// for a week, and it was written down as "harmless" three handovers running.
/// </para>
/// <para>
/// The decision is tested rather than the deleting, because the deleting is one
/// <c>File.Delete</c> and the decision is the part that can be wrong in a way that costs
/// somebody their artwork.
/// </para>
/// </summary>
public class UnusedArtworkTests
{
    private static readonly Guid Channel = Guid.Parse("13953c97-f5a0-4713-8d4c-96b5369e5791");

    private static TvChannel With(Action<ChannelArtwork> set)
    {
        var channel = new TvChannel { Id = Channel, Name = "A channel" };
        set(channel.Artwork);
        return channel;
    }

    private static string Ours(string kind) => $"/LiteTv/Artwork/{Channel}/{kind}";

    [Fact]
    public void AChannelWithNoPicturesSetHasNothingWorthKeeping()
    {
        Assert.Equal(
            new[] { "banner", "backdrop", "poster" },
            LiteTvController.UnusedArtwork(With(_ => { })));
    }

    [Fact]
    public void APictureTheChannelStillPointsAtIsKept()
    {
        var unused = LiteTvController.UnusedArtwork(With(a => a.BannerUrl = Ours("banner")));

        Assert.DoesNotContain("banner", unused);
        Assert.Contains("backdrop", unused);
        Assert.Contains("poster", unused);
    }

    /// <summary>
    /// The page appends a cache-buster to the address it has just uploaded to, so the stored
    /// value is longer than the endpoint. Comparing the two for equality would delete the file
    /// the channel is using, the moment it was uploaded.
    /// </summary>
    [Fact]
    public void ACacheBusterOnTheAddressDoesNotMakeThePictureAnOrphan()
    {
        var unused = LiteTvController.UnusedArtwork(
            With(a => a.BannerUrl = Ours("banner") + "?v=1788005433769"));

        Assert.DoesNotContain("banner", unused);
    }

    [Fact]
    public void ACompactGuidOnTheAddressKeepsThePicture()
    {
        var unused = LiteTvController.UnusedArtwork(
            With(a => a.BannerUrl = $"/LiteTv/Artwork/{Channel:N}/banner?t=1788378491975"));

        Assert.DoesNotContain("banner", unused);
    }

    [Fact]
    public void AnAbsoluteCompactGuidOnTheAddressKeepsThePicture()
    {
        var unused = LiteTvController.UnusedArtwork(
            With(a => a.PosterUrl = $"http://192.168.178.62:8096/LiteTv/Artwork/{Channel:N}/poster?x=1"));

        Assert.DoesNotContain("poster", unused);
    }

    [Fact]
    public void ASimilarButDifferentArtworkRouteDoesNotKeepThePicture()
    {
        Assert.False(LiteTvController.IsOurArtworkUrl(
            $"/LiteTv/Artwork/{Channel:N}/banner-extra", Channel, "banner"));
    }

    [Fact]
    public void APictureReplacedByAnAddressSomewhereElseIsAnOrphan()
    {
        var unused = LiteTvController.UnusedArtwork(
            With(a => a.BackdropUrl = "https://example.invalid/a-picture.jpg"));

        Assert.Contains("backdrop", unused);
    }

    /// <summary>
    /// Borrowing a title's artwork leaves every address null, which is exactly the state a
    /// cleared slot is in - and the uploads that were there before are then unreachable.
    /// </summary>
    [Fact]
    public void BorrowingATitleOrphansEveryUploadTheChannelHad()
    {
        var unused = LiteTvController.UnusedArtwork(With(a => a.ImageItemId = Guid.NewGuid()));

        Assert.Equal(new[] { "banner", "backdrop", "poster" }, unused);
    }

    /// <summary>
    /// One channel's address never keeps another channel's file alive: the id is part of what
    /// is compared, so a copied address is not a reference.
    /// </summary>
    [Fact]
    public void AnotherChannelsAddressDoesNotKeepThisChannelsFile()
    {
        var other = Guid.NewGuid();
        var unused = LiteTvController.UnusedArtwork(
            With(a => a.PosterUrl = $"/LiteTv/Artwork/{other:N}/poster"));

        Assert.Contains("poster", unused);
    }

    [Fact]
    public void MissingArtworkObjectIsSafeToCleanUp()
    {
        var channel = new TvChannel { Id = Channel, Name = "A channel", Artwork = null! };

        Assert.Equal(new[] { "banner", "backdrop", "poster" }, LiteTvController.UnusedArtwork(channel));
    }
}
