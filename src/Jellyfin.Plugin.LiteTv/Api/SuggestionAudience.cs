using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// Who a title is for, in the four bands a channel can be built around.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of one complaint: a channel that mixed Marvel series with
/// <i>Balu und seine Crew</i>. Both are Disney by studio and both are "animation" by genre, so
/// every signal the suggestions used said they belonged together. The signal that says they do
/// not is the age they are made for, and nothing was reading it.
/// </para>
/// <para>
/// The ordering matters as much as the naming: bands are compared, so <see cref="Child"/> has to
/// sit next to <see cref="Family"/> and two steps from <see cref="Teen"/>. Adjacency is what
/// lets a family channel carry the gentler end of children's programming without letting a
/// teen action channel do the same.
/// </para>
/// </remarks>
internal enum AudienceBand
{
    /// <summary>No usable rating. Deliberately not a band of its own on the scale.</summary>
    Unknown = 0,

    /// <summary>Preschool and young children: FSK 0, G, TV-Y, TV-G.</summary>
    Child = 1,

    /// <summary>Watchable together: FSK 6, PG, TV-Y7, TV-PG.</summary>
    Family = 2,

    /// <summary>Older children and teenagers: FSK 12 and 16, PG-13, TV-14.</summary>
    Teen = 3,

    /// <summary>Adults: FSK 18, R, NC-17, TV-MA.</summary>
    Adult = 4
}

/// <summary>
/// Reads an official rating into an <see cref="AudienceBand"/>.
/// </summary>
internal static class SuggestionAudience
{
    /// <summary>
    /// Reads one item's band.
    /// </summary>
    /// <param name="item">The title.</param>
    /// <returns>The band, or <see cref="AudienceBand.Unknown"/> when nothing says.</returns>
    internal static AudienceBand Of(BaseItem item) => Of(item.OfficialRating);

    /// <summary>
    /// Reads a rating string into a band.
    /// </summary>
    /// <remarks>
    /// Ratings arrive in whatever shape the metadata provider used: <c>FSK-12</c>, <c>FSK 12</c>,
    /// <c>de/12</c>, a bare <c>12</c>, or an American code. Rather than enumerate every spelling,
    /// this pulls the first number out and falls back to the named codes - a number is the part
    /// that is unambiguous across all of them, and the codes are a short closed list.
    /// </remarks>
    /// <param name="rating">The official rating as the server stored it.</param>
    /// <returns>The band.</returns>
    internal static AudienceBand Of(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return AudienceBand.Unknown;
        }

        var text = rating.Trim();

        // The named codes first: "PG-13" contains a 13, and reading that as an age would land it
        // in the same band by luck, while "TV-Y7" would become a seven-year-old's band by luck
        // and "NC-17" would become an adult's by luck. Luck is not a rule.
        switch (text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
        {
            case "G":
            case "TV-Y":
            case "TV-G":
            case "U":
            case "0+":
                return AudienceBand.Child;
            case "PG":
            case "TV-Y7":
            case "TV-PG":
            case "6+":
                return AudienceBand.Family;
            case "PG-13":
            case "TV-14":
            case "12":
            case "12A":
            case "15":
                return AudienceBand.Teen;
            case "R":
            case "NC-17":
            case "TV-MA":
            case "18":
            case "X":
                return AudienceBand.Adult;
            default:
                break;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var age))
        {
            return AudienceBand.Unknown;
        }

        return age switch
        {
            <= 0 => AudienceBand.Child,
            <= 6 => AudienceBand.Family,
            <= 16 => AudienceBand.Teen,
            _ => AudienceBand.Adult
        };
    }

    /// <summary>
    /// Whether a title belongs on a channel built for a band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exact, with one deliberate widening: a <see cref="AudienceBand.Family"/> channel accepts
    /// <see cref="AudienceBand.Child"/> titles. That direction is safe and it is what a family
    /// channel is - the reverse never is, which is the whole complaint this answers.
    /// </para>
    /// <para>
    /// An unrated title is accepted only where being wrong is survivable: on an adult or teen
    /// channel it is a title somebody may not have wanted, on a children's channel it is a
    /// title nobody checked. So <see cref="AudienceBand.Child"/> and
    /// <see cref="AudienceBand.Family"/> take rated titles only.
    /// </para>
    /// </remarks>
    /// <param name="item">The band the title carries.</param>
    /// <param name="channel">The band the channel is being built for.</param>
    /// <returns>Whether it belongs.</returns>
    internal static bool Fits(AudienceBand item, AudienceBand channel) => channel switch
    {
        AudienceBand.Unknown => true,
        AudienceBand.Child => item == AudienceBand.Child,
        AudienceBand.Family => item is AudienceBand.Child or AudienceBand.Family,
        AudienceBand.Teen => item is AudienceBand.Teen or AudienceBand.Unknown,
        AudienceBand.Adult => item is AudienceBand.Adult or AudienceBand.Unknown,
        _ => true
    };

    /// <summary>
    /// The band a mixed pool is really about, so a generated channel can be made coherent
    /// without anybody having chosen a band.
    /// </summary>
    /// <remarks>
    /// The most common rated band wins, and unrated titles do not vote: they are the reason a
    /// pool looks bandless in the first place, and letting them decide would hand the answer to
    /// the titles that know least.
    /// </remarks>
    /// <param name="items">The pool.</param>
    /// <returns>The dominant band, or unknown when nothing in the pool is rated.</returns>
    internal static AudienceBand Dominant(IEnumerable<BaseItem> items)
    {
        var counted = items
            .Select(Of)
            .Where(band => band != AudienceBand.Unknown)
            .GroupBy(band => band)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .FirstOrDefault();

        return counted?.Key ?? AudienceBand.Unknown;
    }

    /// <summary>The band's name for the configuration page, in German.</summary>
    /// <param name="band">The band.</param>
    /// <returns>The words.</returns>
    internal static string Words(AudienceBand band) => band switch
    {
        AudienceBand.Child => "Kinder",
        AudienceBand.Family => "Familie",
        AudienceBand.Teen => "Jugendliche",
        AudienceBand.Adult => "Erwachsene",
        _ => "ohne Altersangabe"
    };

    /// <summary>Reads the band the configuration page asked for.</summary>
    /// <param name="requested">The wire value: child, family, teen, adult or anything else.</param>
    /// <returns>The band, or unknown to mean "no restriction".</returns>
    internal static AudienceBand Requested(string? requested) => requested?.ToLowerInvariant() switch
    {
        "child" or "kinder" => AudienceBand.Child,
        "family" or "familie" => AudienceBand.Family,
        "teen" or "jugend" or "jugendliche" => AudienceBand.Teen,
        "adult" or "erwachsene" => AudienceBand.Adult,
        _ => AudienceBand.Unknown
    };
}
