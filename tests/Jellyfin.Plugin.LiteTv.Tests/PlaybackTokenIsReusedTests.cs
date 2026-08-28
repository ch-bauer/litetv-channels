using System;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.LiteTv.Sessions;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// The playback account's token must be reused while it is still good.
/// <para>
/// Found on 27 Aug 2026 by breaking the owner's television mid-playback, and the oldest fault
/// this plugin had. <see cref="ChannelPlaybackUser"/> authenticates with <b>one constant device
/// id</b>, and Jellyfin keeps one session per device id - so every authentication of the account
/// revokes the token the previous holder is using. The credentials lived in memory only, so a
/// restart, a plugin install, or a second client tuning in authenticated afresh and killed a
/// stream that was playing. On a television that is not an error, it is a video that loads for
/// ever, because the stream's own requests are being refused.
/// </para>
/// <para>
/// The token is stored now and checked before another is minted. These tests cover the check
/// that decides it, because that decision is the whole fix: answer yes and nothing is
/// interrupted, answer no and somebody's stream ends.
/// </para>
/// <para>
/// What they cannot cover is the call they prevent. Authenticating goes through
/// <c>Plugin.Instance</c> and a real session manager, so "and therefore it did not authenticate"
/// is left to the log line on each path - which is why both paths have one, saying which
/// happened and what it costs.
/// </para>
/// </summary>
public class PlaybackTokenIsReusedTests
{
    private const string Token = "1f8b0a5c8e2d4b7a9c3e5f1d2a4b6c8e";

    private readonly IDeviceManager _devices = Substitute.For<IDeviceManager>();

    private ChannelPlaybackUser Subject() => new(
        Substitute.For<IUserManager>(),
        Substitute.For<ISessionManager>(),
        _devices,
        NullLogger<ChannelPlaybackUser>.Instance);

    /// <summary>Sets what the server says it knows about a token.</summary>
    private void ServerHolds(params Device[] found) =>
        _devices.GetDevices(Arg.Any<DeviceQuery>())
            .Returns(new QueryResult<Device>(found));

    private static Device DeviceFor(Guid userId, string token)
    {
        var device = new Device(userId, "LiteTV", "1.0.0", "LiteTV Channels", "litetv-channel-playback");
        device.AccessToken = token;
        return device;
    }

    [Fact]
    public void A_token_the_server_still_knows_is_reused()
    {
        var user = Guid.NewGuid();
        ServerHolds(DeviceFor(user, Token));

        Assert.True(Subject().IsAlive(Token, user));
    }

    [Fact]
    public void A_token_the_server_has_forgotten_is_not_reused()
    {
        ServerHolds();

        Assert.False(Subject().IsAlive(Token, Guid.NewGuid()));
    }

    /// <summary>
    /// Renaming the configured account resolves a different user, and the token left behind
    /// belongs to the old one. Reusing it would play as the wrong account - which is the single
    /// thing this whole class exists to prevent - so a live token is not enough on its own.
    /// </summary>
    [Fact]
    public void A_live_token_belonging_to_another_account_is_not_reused()
    {
        var somebodyElse = Guid.NewGuid();
        ServerHolds(DeviceFor(somebodyElse, Token));

        Assert.False(Subject().IsAlive(Token, Guid.NewGuid()));
    }

    [Fact]
    public void Nothing_stored_is_not_reused()
    {
        Assert.False(Subject().IsAlive(string.Empty, Guid.NewGuid()));

        // And it did not go asking, because there is nothing to ask about.
        _devices.DidNotReceive().GetDevices(Arg.Any<DeviceQuery>());
    }

    /// <summary>
    /// Not knowing is answered by minting a new token, which costs a stream but not the
    /// feature. It must never be answered by throwing: this runs on the path a client takes to
    /// start playing, and a failure here would stop the channel rather than interrupt it.
    /// </summary>
    [Fact]
    public void A_server_that_cannot_answer_is_treated_as_dead_rather_than_thrown()
    {
        _devices.GetDevices(Arg.Any<DeviceQuery>())
            .Returns<QueryResult<Device>>(_ => throw new InvalidOperationException("no database"));

        Assert.False(Subject().IsAlive(Token, Guid.NewGuid()));
    }

    /// <summary>
    /// The query has to ask about the token itself. Asking by device id would answer for
    /// whatever session happens to hold that device now, which is a different question and one
    /// that would say "alive" about a token that had just been revoked.
    /// </summary>
    [Fact]
    public void The_question_asked_is_about_the_token()
    {
        var user = Guid.NewGuid();
        ServerHolds(DeviceFor(user, Token));

        Subject().IsAlive(Token, user);

        _devices.Received(1).GetDevices(Arg.Is<DeviceQuery>(q => q.AccessToken == Token));
    }
}
