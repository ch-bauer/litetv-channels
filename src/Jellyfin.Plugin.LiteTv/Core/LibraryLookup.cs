using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// Asking the library for an item that may not be one.
/// <para>
/// <b>Jellyfin's <c>GetItemById</c> throws on an empty guid</b> - <c>ArgumentException: Guid
/// can't be empty</c> - rather than answering null. Half of what LiteTV schedules has no
/// library item behind it at all: an advert is an address, a trailer is an address, and a
/// break announcing nothing has no programme to name. Every one of those carries
/// <see cref="System.Guid.Empty"/>, and every unguarded lookup is a request that dies.
/// </para>
/// <para>
/// It cost the app its entire TV section, intermittently and invisibly: one advert on air with
/// nothing trailed made <c>GET /LiteTv/Channels</c> answer 400, the app read that as "this
/// server does not have the plugin", and the channels vanished from the navigation drawer with
/// nothing anywhere saying why. Whether it happened depended on what was airing at the moment
/// somebody opened the app.
/// </para>
/// </summary>
public static class LibraryLookup
{
    /// <summary>
    /// The item with this id, or null - including when the id is empty, which means "there is
    /// no item here" rather than "look one up".
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="id">The item's id, or an empty guid for nothing.</param>
    /// <returns>The item, or null.</returns>
    public static BaseItem? Find(this ILibraryManager library, Guid id)
        => id == Guid.Empty ? null : library.GetItemById(id);
}
