using System;
using Jellyfin.Plugin.LiteTv.Api;
using Jellyfin.Plugin.LiteTv.Core;
using MediaBrowser.Controller.Library;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiteTv.Tests;

/// <summary>
/// How long something dragged onto the week runs.
/// <para>
/// The page sends no length on purpose - a typed length has twice been a control that lied -
/// and the server used to store that nothing as a zero-second row. A zero-second row is drawn
/// as a hairline and plays for no time at all, which is exactly what "drag and drop does not
/// work" looked like from the outside.
/// </para>
/// </summary>
public class PlacedLengthTests
{
    [Fact]
    public void ALengthTheCallerKnows_IsKept()
    {
        // Moving a row sends the length it already has, and nothing may second-guess it.
        Assert.Equal(5400, LiteTvController.LengthOf(5400, TimeSpan.FromHours(2).Ticks));
    }

    [Fact]
    public void NoLengthSent_ComesFromTheItemsRuntime()
    {
        var ticks = TimeSpan.FromMinutes(97).Ticks;
        Assert.Equal(97 * 60, LiteTvController.LengthOf(0, ticks));
    }

    [Fact]
    public void NoLengthAndNoRuntime_IsVisibleRatherThanNothing()
    {
        // An address, or an item the library never measured.
        Assert.Equal(LiteTvController.UnknownLengthSeconds, LiteTvController.LengthOf(0, 0));
        Assert.True(LiteTvController.LengthOf(0, 0) > 0);
    }

    [Fact]
    public void ANegativeLength_IsTreatedAsNoneRatherThanStored()
    {
        Assert.Equal(LiteTvController.UnknownLengthSeconds, LiteTvController.LengthOf(-30, 0));
    }

    [Fact]
    public void ARuntimeTooShortForTheWeekToHold_FallsBackRatherThanRoundingToNothing()
    {
        // A runtime of a fraction of a second rounds to zero, and the week drops anything under
        // fifteen seconds as a sliver. Either way the row would vanish, so it gets the fallback.
        Assert.Equal(LiteTvController.UnknownLengthSeconds, LiteTvController.LengthOf(0, TimeSpan.FromMilliseconds(200).Ticks));
        Assert.Equal(LiteTvController.UnknownLengthSeconds, LiteTvController.LengthOf(0, TimeSpan.FromSeconds(9).Ticks));
    }

    [Fact]
    public void AShortIdentTheWeekCanHold_KeepsItsOwnLength()
    {
        Assert.Equal(30, LiteTvController.LengthOf(0, TimeSpan.FromSeconds(30).Ticks));
    }
}

/// <summary>
/// Half of what a channel schedules has no library item behind it - an advert is an address, a
/// trailer is an address, and a break announcing nothing has no programme to name. Every one of
/// those carries an empty guid, and Jellyfin's <c>GetItemById</c> <b>throws</b> on one rather
/// than answering null.
/// <para>
/// One advert on air with nothing trailed made <c>GET /LiteTv/Channels</c> answer 400; the app
/// reads a failure there as "this server has no LiteTV" and drops the whole TV section from its
/// navigation drawer. Nothing anywhere said why, and whether it happened at all depended on what
/// was airing at the moment somebody opened the app.
/// </para>
/// </summary>
public class EmptyIdTests
{
    [Fact]
    public void AnEmptyId_MeansNoItem_AndTheLibraryIsNeverAsked()
    {
        var library = Substitute.For<ILibraryManager>();
        library.GetItemById(Arg.Any<Guid>())
            .Returns(_ => throw new ArgumentException("Guid can't be empty", "id"));

        // No throw, no lookup: an empty id is the answer, not a question.
        Assert.Null(library.Find(Guid.Empty));
        library.DidNotReceive().GetItemById(Arg.Any<Guid>());
    }

    [Fact]
    public void ARealId_IsAsked()
    {
        var library = Substitute.For<ILibraryManager>();
        var id = Guid.NewGuid();

        library.Find(id);

        library.Received(1).GetItemById(id);
    }
}
