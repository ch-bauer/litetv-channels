namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// Decides which studio a studio channel's own artwork should come from.
/// <para>
/// Pulled out of <see cref="LiteTvController"/> so the selection rule - most common studio
/// string first, first one that actually carries a picture wins - can be proven without a
/// library manager or an HTTP context behind it. The lookup itself, which does need both, is a
/// delegate the caller supplies.
/// </para>
/// </summary>
internal static class StudioArtworkResolver
{
    /// <summary>
    /// Picks the studio to borrow artwork from.
    /// </summary>
    /// <param name="studiosPerSource">
    /// The <c>Studios</c> metadata of each title the channel ended up with. One entry per title;
    /// a title with no studio metadata contributes nothing.
    /// </param>
    /// <param name="lookup">
    /// Answers whether a studio name names a library item with any picture at all, returning its
    /// id and its own name when it does, or null when it does not exist or has nothing to show.
    /// </param>
    /// <returns>The chosen studio's artwork, or null when none of the candidates has a picture.</returns>
    internal static SuggestedArtworkDto? Resolve(
        IEnumerable<IReadOnlyList<string>> studiosPerSource,
        Func<string, (Guid Id, string Name)?> lookup)
    {
        var ranked = studiosPerSource
            .SelectMany(names => names)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key);

        foreach (var name in ranked)
        {
            if (lookup(name) is { } found)
            {
                return new SuggestedArtworkDto { ItemId = found.Id, ItemName = found.Name };
            }
        }

        return null;
    }
}
