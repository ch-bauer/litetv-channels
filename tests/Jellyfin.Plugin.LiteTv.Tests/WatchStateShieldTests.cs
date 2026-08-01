using Jellyfin.Plugin.LiteTv.Sessions;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class WatchStateShieldTests
{
    private const string Session = "session-1";
    private const string OtherSession = "session-2";
    private const string LivingRoom = "device-living-room";
    private const string Tablet = "device-tablet";

    private static readonly TimeSpan NoGrace = TimeSpan.Zero;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();

    /// <summary>Whether a write reported by this device would be dropped.</summary>
    private bool IsShieldedFor(WatchStateShield shield, string? deviceId, Guid? user = null, Guid? item = null)
    {
        if (!shield.TryGetShieldedDevices(user ?? _user, item ?? _item, out var deviceIds))
        {
            return false;
        }

        return deviceIds.Count == 0 || deviceId is null || deviceIds.Contains(deviceId, StringComparer.Ordinal);
    }

    [Fact]
    public void UnknownItem_IsNotShielded()
    {
        var shield = new WatchStateShield();

        Assert.False(shield.TryGetShieldedDevices(_user, _item, out _));
    }

    [Fact]
    public void Arm_ShieldsOnlyThatUserAndItem()
    {
        var shield = new WatchStateShield();

        shield.Arm(Session, LivingRoom, _user, _item);

        Assert.True(shield.TryGetShieldedDevices(_user, _item, out _));
        Assert.False(shield.TryGetShieldedDevices(Guid.NewGuid(), _item, out _));
        Assert.False(shield.TryGetShieldedDevices(_user, Guid.NewGuid(), out _));
    }

    /// <summary>
    /// The same title watched deliberately on another device while it happens to be on air
    /// has to keep its watch state.
    /// </summary>
    [Fact]
    public void Arm_CoversOnlyTheDeviceTheChannelPlaysOn()
    {
        var shield = new WatchStateShield();

        shield.Arm(Session, LivingRoom, _user, _item);

        Assert.True(IsShieldedFor(shield, LivingRoom));
        Assert.False(IsShieldedFor(shield, Tablet));
    }

    [Fact]
    public void Arm_WithUnknownDevice_CoversEverywhere()
    {
        var shield = new WatchStateShield();

        shield.Arm(Session, null, _user, _item);

        Assert.True(IsShieldedFor(shield, LivingRoom));
        Assert.True(IsShieldedFor(shield, Tablet));
    }

    [Fact]
    public void Arm_OnTwoDevices_CoversBothAndReleasesThemSeparately()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Arm(OtherSession, Tablet, _user, _item);

        Assert.True(IsShieldedFor(shield, LivingRoom));
        Assert.True(IsShieldedFor(shield, Tablet));

        shield.Release(Session, _user, _item, NoGrace);

        Assert.False(IsShieldedFor(shield, LivingRoom));
        Assert.True(IsShieldedFor(shield, Tablet));
    }

    [Fact]
    public void Release_WithoutGrace_StopsShielding()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);

        Assert.False(IsShieldedFor(shield, LivingRoom));
    }

    [Fact]
    public void Release_WithGrace_KeepsShieldingTheDeviceForTheTrailingReports()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);

        shield.Release(Session, _user, _item, Grace);

        Assert.True(IsShieldedFor(shield, LivingRoom));
        Assert.False(IsShieldedFor(shield, Tablet));
    }

    /// <summary>
    /// The replay case: the script registers the item again before the stop of the playback
    /// it replaces arrives, so that stop must not uncover the playback that just started.
    /// </summary>
    [Fact]
    public void Release_WhileAnotherPlaybackOfTheSameItemIsArmed_KeepsShielding()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Arm(Session, LivingRoom, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);

        Assert.True(IsShieldedFor(shield, LivingRoom));

        shield.Release(Session, _user, _item, NoGrace);

        Assert.False(IsShieldedFor(shield, LivingRoom));
    }

    [Fact]
    public void Release_MoreOftenThanArmed_DoesNotUncoverAnotherSession()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Arm(OtherSession, LivingRoom, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);
        shield.Release(Session, _user, _item, NoGrace);

        Assert.True(IsShieldedFor(shield, LivingRoom));
    }

    [Fact]
    public void ReleaseSession_ReleasesEverythingItHolds()
    {
        var shield = new WatchStateShield();
        var second = Guid.NewGuid();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Arm(Session, LivingRoom, _user, second);

        shield.ReleaseSession(Session, NoGrace);

        Assert.False(shield.TryGetShieldedDevices(_user, _item, out _));
        Assert.False(shield.TryGetShieldedDevices(_user, second, out _));
    }

    [Fact]
    public void ReleaseSession_LeavesWhatAnotherSessionHolds()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Arm(OtherSession, Tablet, _user, _item);

        shield.ReleaseSession(Session, NoGrace);

        Assert.True(IsShieldedFor(shield, Tablet));
        Assert.False(IsShieldedFor(shield, LivingRoom));

        shield.ReleaseSession(OtherSession, NoGrace);

        Assert.False(shield.TryGetShieldedDevices(_user, _item, out _));
    }

    [Fact]
    public void ReleaseSession_UnknownSession_DoesNothing()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);

        shield.ReleaseSession(OtherSession, NoGrace);

        Assert.True(IsShieldedFor(shield, LivingRoom));
    }

    [Fact]
    public void Arm_AfterRelease_ShieldsAgain()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, LivingRoom, _user, _item);
        shield.Release(Session, _user, _item, NoGrace);

        shield.Arm(Session, LivingRoom, _user, _item);

        Assert.True(IsShieldedFor(shield, LivingRoom));
    }
}
