using Jellyfin.Plugin.LiteTv.Sessions;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

public class WatchStateShieldTests
{
    private const string Session = "session-1";
    private const string OtherSession = "session-2";

    private static readonly TimeSpan NoGrace = TimeSpan.Zero;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();

    [Fact]
    public void IsShielded_UnknownItem_IsFalse()
    {
        var shield = new WatchStateShield();

        Assert.False(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void Arm_ShieldsOnlyThatUserAndItem()
    {
        var shield = new WatchStateShield();

        shield.Arm(Session, _user, _item);

        Assert.True(shield.IsShielded(_user, _item));
        Assert.False(shield.IsShielded(Guid.NewGuid(), _item));
        Assert.False(shield.IsShielded(_user, Guid.NewGuid()));
    }

    [Fact]
    public void Release_WithoutGrace_StopsShielding()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);

        Assert.False(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void Release_WithGrace_KeepsShieldingForTheTrailingReports()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);

        shield.Release(Session, _user, _item, Grace);

        Assert.True(shield.IsShielded(_user, _item));
    }

    /// <summary>
    /// The replay case: the script registers the item again before the stop of the playback
    /// it replaces arrives, so that stop must not uncover the playback that just started.
    /// </summary>
    [Fact]
    public void Release_WhileAnotherPlaybackOfTheSameItemIsArmed_KeepsShielding()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);
        shield.Arm(Session, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);

        Assert.True(shield.IsShielded(_user, _item));

        shield.Release(Session, _user, _item, NoGrace);

        Assert.False(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void Release_MoreOftenThanArmed_DoesNotUncoverAnotherSession()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);
        shield.Arm(OtherSession, _user, _item);

        shield.Release(Session, _user, _item, NoGrace);
        shield.Release(Session, _user, _item, NoGrace);

        Assert.True(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void ReleaseSession_ReleasesEverythingItHolds()
    {
        var shield = new WatchStateShield();
        var second = Guid.NewGuid();
        shield.Arm(Session, _user, _item);
        shield.Arm(Session, _user, second);

        shield.ReleaseSession(Session, NoGrace);

        Assert.False(shield.IsShielded(_user, _item));
        Assert.False(shield.IsShielded(_user, second));
    }

    [Fact]
    public void ReleaseSession_LeavesWhatAnotherSessionHolds()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);
        shield.Arm(OtherSession, _user, _item);

        shield.ReleaseSession(Session, NoGrace);

        Assert.True(shield.IsShielded(_user, _item));

        shield.ReleaseSession(OtherSession, NoGrace);

        Assert.False(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void ReleaseSession_UnknownSession_DoesNothing()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);

        shield.ReleaseSession(OtherSession, NoGrace);

        Assert.True(shield.IsShielded(_user, _item));
    }

    [Fact]
    public void Arm_AfterRelease_ShieldsAgain()
    {
        var shield = new WatchStateShield();
        shield.Arm(Session, _user, _item);
        shield.Release(Session, _user, _item, NoGrace);

        shield.Arm(Session, _user, _item);

        Assert.True(shield.IsShielded(_user, _item));
    }
}
