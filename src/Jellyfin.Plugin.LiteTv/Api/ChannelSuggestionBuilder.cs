using Jellyfin.Plugin.LiteTv.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.LiteTv.Api;

/// <summary>
/// What the configuration page asked for before any suggestion was built.
/// </summary>
/// <param name="Audience">
/// The band to build for, or <see cref="AudienceBand.Unknown"/> to let each candidate settle on
/// its own.
/// </param>
/// <param name="MaxTitles">
/// The largest schedule a proposal may expand to, counted in playable titles - episodes, not
/// series. This is the answer to a suggestion that quietly proposed 453 of them.
/// </param>
/// <param name="Families">
/// The kinds of channel to offer, by <see cref="SuggestionFamily"/> name. Empty means all.
/// </param>
/// <param name="Refresh">
/// Which turn of the wheel this is. Asking again with a higher number offers different ideas
/// rather than the same ones reordered.
/// </param>
/// <param name="Dismissed">Names the owner has already said no to.</param>
/// <param name="Strictness">
/// How tightly the titles must belong together, 0 to 100. It is the floor on the similarity
/// score, and it is the setting that decides whether a studio channel is a studio channel or a
/// list of everything that studio ever put its name on.
/// </param>
/// <param name="FilmNight">
/// <c>auto</c>, <c>on</c> or <c>off</c>. A film channel never gets one whatever this says: a
/// block of films inside a channel that is already films is the same programme twice.
/// </param>
/// <param name="Trailers">Whether proposals come with the trailer preview turned on.</param>
/// <param name="RandomizeEpisodes">Whether a series' episodes are mixed before selection.</param>
/// <param name="MinSources">The fewest sources a proposal may be built from.</param>
/// <param name="MaxSources">The most sources a proposal may be built from.</param>
internal sealed record SuggestionOptions(
    AudienceBand Audience,
    int MaxTitles,
    IReadOnlyCollection<string> Families,
    int Refresh,
    IReadOnlyCollection<string> Dismissed,
    int Strictness = 45,
    string FilmNight = "auto",
    bool Trailers = true,
    bool RandomizeEpisodes = true,
    int MinSources = 3,
    int MaxSources = 12)
{
    /// <summary>The default: everything, at a size that fills an evening without a marathon.</summary>
    internal static SuggestionOptions Default { get; } =
        new(AudienceBand.Unknown, 60, Array.Empty<string>(), 0, Array.Empty<string>());

    /// <summary>Whether a film night may be offered for a channel of this family.</summary>
    /// <param name="family">The family being composed.</param>
    /// <returns>Whether to consider one at all.</returns>
    internal bool WantsFilmNight(string family) =>
        !string.Equals(family, SuggestionFamily.Film, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(FilmNight, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a film night should be offered even where it is a squeeze.</summary>
    internal bool InsistsOnFilmNight =>
        string.Equals(FilmNight, "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a family of channel was asked for.</summary>
    /// <param name="family">The family.</param>
    /// <returns>True when it was asked for, or when nothing was.</returns>
    internal bool Wants(string family) =>
        Families.Count == 0 || Families.Contains(family, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The kinds of channel that can be proposed. On the wire as these names.</summary>
internal static class SuggestionFamily
{
    /// <summary>A studio or franchise: Disney, DreamWorks, and whatever else the metadata carries.</summary>
    internal const string Studio = "studio";

    /// <summary>Children's programming, built from the rating rather than from a genre label.</summary>
    internal const string Kids = "kids";

    /// <summary>Documentary and factual.</summary>
    internal const string Factual = "factual";

    /// <summary>A genre the library happens to be rich in.</summary>
    internal const string Genre = "genre";

    /// <summary>Films only, no episodes.</summary>
    internal const string Film = "film";

    /// <summary>A collection played through. Built by the controller, which can resolve a BoxSet.</summary>
    internal const string Collection = "collection";

    /// <summary>Every family, in the order they are offered.</summary>
    internal static IReadOnlyList<string> All { get; } = new[] { Studio, Kids, Factual, Genre, Film, Collection };
}

/// <summary>
/// Turns the library into ready-to-air channel ideas. It describes a programme identity rather
/// than borrowing a broadcaster's name: the suggestion is about the owner's media, not an
/// assertion that it is an official channel.
/// </summary>
/// <remarks>
/// <para>
/// Three things decide whether an idea is offered, and all three came from the same report - a
/// channel that mixed Marvel with <i>Balu und seine Crew</i> and expanded to 453 titles without
/// saying so.
/// </para>
/// <list type="number">
/// <item>
/// <b>It has to be coherent.</b> A pool is trimmed to the band it is mostly about, so a channel
/// is for somebody in particular rather than for whoever the genre label caught.
/// </item>
/// <item>
/// <b>It has to be bounded.</b> Sources are taken while they fit the requested size, counted in
/// episodes rather than in series, and the count is on the wire so the page can show it before
/// anybody adds the channel.
/// </item>
/// <item>
/// <b>It has to be new.</b> Existing channels, dismissed ideas and the ideas just shown are all
/// skipped, and asking again turns the wheel rather than reordering the same handful.
/// </item>
/// </list>
/// </remarks>
internal static class ChannelSuggestionBuilder
{
    /// <summary>How many proposals are offered at once.</summary>
    private const int Offered = 6;

    /// <summary>
    /// Builds the useful, distinct channel templates the current library can support.
    /// </summary>
    /// <param name="series">The series available, already filtered to the chosen libraries.</param>
    /// <param name="movies">The films available, already filtered to the chosen libraries.</param>
    /// <param name="existingNames">Channels that already exist, by name.</param>
    /// <param name="options">What the page asked for.</param>
    /// <param name="episodeCount">
    /// How many playable episodes a series expands to. Supplied by the caller because only it
    /// can ask the library, and because a test can then state the number outright.
    /// </param>
    /// <param name="libraries">The libraries the pool was taken from, for the stated reason.</param>
    /// <param name="cohesion">
    /// Trims a pool to the titles that really belong together, given the pool and the anchor it
    /// is mostly about. Supplied by the caller because the good answer comes from the Smart
    /// Similar plugin over HTTP; null keeps every title, which is what a test wants unless it is
    /// testing this.
    /// </param>
    /// <returns>The proposals, most interesting first.</returns>
    internal static List<ChannelSuggestionDto> Build(
        IEnumerable<Series> series,
        IEnumerable<Movie> movies,
        IEnumerable<string> existingNames,
        SuggestionOptions? options = null,
        Func<Series, int>? episodeCount = null,
        IReadOnlyCollection<string>? libraries = null,
        Func<IReadOnlyList<BaseItem>, BaseItem, IReadOnlyList<BaseItem>>? cohesion = null)
    {
        var settings = options ?? SuggestionOptions.Default;
        var count = episodeCount ?? (_ => 1);
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        foreach (var dismissed in settings.Dismissed)
        {
            taken.Add(dismissed);
        }

        var all = series.Cast<BaseItem>().Concat(movies)
            .Where(item => item.Id != Guid.Empty)
            .DistinctBy(item => item.Id)
            .ToList();

        // Asked for a band, every candidate is drawn from titles that fit it. Asked for nothing,
        // the whole library is in play and each candidate settles on its own band below.
        var pool = settings.Audience == AudienceBand.Unknown
            ? all
            : all.Where(item => SuggestionAudience.Fits(SuggestionAudience.Of(item), settings.Audience)).ToList();

        var candidates = new List<Candidate>();
        AddStudioChannels(candidates, pool, settings);
        AddKidsChannel(candidates, pool, settings);
        AddFactualChannel(candidates, pool, settings);
        AddGenreChannels(candidates, pool, settings);
        AddFilmChannel(candidates, pool, settings);

        var result = new List<ChannelSuggestionDto>();
        foreach (var candidate in Rotate(candidates, settings.Refresh))
        {
            if (result.Count >= Offered || taken.Contains(candidate.Name))
            {
                continue;
            }

            var built = Compose(candidate, settings, count, libraries ?? Array.Empty<string>(), cohesion);
            if (built is null)
            {
                continue;
            }

            taken.Add(candidate.Name);
            result.Add(built);
        }

        return result;
    }

    /// <summary>
    /// A proposal before it has been costed: an identity and the titles it could be built from.
    /// </summary>
    private sealed record Candidate(
        string Name,
        string Description,
        string Theme,
        string Family,
        IReadOnlyList<BaseItem> Pool,
        IReadOnlyList<string> Because);

    /// <summary>
    /// Offers a different set of ideas each time it is asked, without inventing new ones.
    /// </summary>
    /// <remarks>
    /// A rotation rather than a shuffle. The order candidates were generated in is a real
    /// ranking - the studio the library has most of comes before the genre it has fewest of -
    /// and shuffling would throw that away to answer a complaint that was only about seeing the
    /// same six twice. Turning the wheel keeps the ranking and changes who is at the front.
    /// </remarks>
    /// <param name="candidates">The candidates, best first.</param>
    /// <param name="refresh">How many times the owner has asked for different ideas.</param>
    /// <returns>The candidates, rotated.</returns>
    private static IEnumerable<Candidate> Rotate(IReadOnlyList<Candidate> candidates, int refresh)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var turns = ((refresh % candidates.Count) + candidates.Count) % candidates.Count;
        return candidates.Skip(turns).Concat(candidates.Take(turns));
    }

    private static void AddStudioChannels(List<Candidate> candidates, IReadOnlyList<BaseItem> pool, SuggestionOptions options)
    {
        if (!options.Wants(SuggestionFamily.Studio))
        {
            return;
        }

        // Named studios first, because these are the channels somebody actually asks for, and
        // because the name of the channel should be the name of the studio rather than whichever
        // spelling the metadata used. Pixar sits under Disney deliberately: a Pixar-only channel
        // out of a normal library is four films.
        var named = new (string Name, string Theme, string[] Studios)[]
        {
            ("Disney & Pixar", "Familie & Animation", ["disney", "pixar"]),
            ("DreamWorks", "Familie & Animation", ["dreamworks"]),
            ("Marvel & Lucasfilm", "Action & Abenteuer", ["marvel", "lucasfilm"]),
            ("Warner-Kino", "Kino", ["warner"]),
            ("Nickelodeon", "Kinderprogramm", ["nickelodeon"]),
            ("Cartoon Network", "Animation", ["cartoon network"])
        };

        foreach (var studio in named)
        {
            var titles = pool.Where(item => HasStudio(item, studio.Studios)).ToList();
            if (titles.Count < options.MinSources)
            {
                continue;
            }

            candidates.Add(new Candidate(
                studio.Name,
                "Titel, deren Studio-Metadaten " + string.Join(" oder ", studio.Studios.Select(Capitalise)) + " nennen.",
                studio.Theme,
                SuggestionFamily.Studio,
                titles,
                ["Studio: " + string.Join(", ", studio.Studios.Select(Capitalise))]));
        }
    }

    private static void AddKidsChannel(List<Candidate> candidates, IReadOnlyList<BaseItem> pool, SuggestionOptions options)
    {
        if (!options.Wants(SuggestionFamily.Kids))
        {
            return;
        }

        // The rating, not the genre. "Animation" is what put a teen action series on a
        // children's channel; an age rating is the one field that was written to answer this
        // exact question, and a title without one is left off rather than guessed at.
        var titles = pool.Where(item => SuggestionAudience.Of(item) is AudienceBand.Child or AudienceBand.Family).ToList();
        if (titles.Count < options.MinSources)
        {
            return;
        }

        candidates.Add(new Candidate(
            "Kinderzeit",
            "Titel mit einer Altersfreigabe für Kinder, als abwechslungsreiches Tagesprogramm.",
            "Kinderprogramm",
            SuggestionFamily.Kids,
            titles,
            ["Altersfreigabe: 0 bis 6"]));
    }

    private static void AddFactualChannel(List<Candidate> candidates, IReadOnlyList<BaseItem> pool, SuggestionOptions options)
    {
        if (!options.Wants(SuggestionFamily.Factual))
        {
            return;
        }

        var titles = WithGenre(pool, "Documentary", "Dokumentation", "Reality", "History", "Geschichte", "Nature").ToList();
        if (titles.Count < options.MinSources)
        {
            return;
        }

        candidates.Add(new Candidate(
            "Werkstatt & Wildnis",
            "Raues Faktenfernsehen aus Dokus, Reality- und Naturtiteln deiner Bibliothek.",
            "Fakten & Abenteuer",
            SuggestionFamily.Factual,
            titles,
            ["Genres: Dokumentation, Reality, Natur"]));
    }

    private static void AddGenreChannels(List<Candidate> candidates, IReadOnlyList<BaseItem> pool, SuggestionOptions options)
    {
        if (!options.Wants(SuggestionFamily.Genre))
        {
            return;
        }

        foreach (var genre in ByGenre(pool)
            .Where(pair => pair.Value.Count >= 4)
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            var profile = GenreProfile(genre.Key);
            candidates.Add(new Candidate(
                profile.Name,
                genre.Value.Count + " lokale Titel mit " + genre.Key + ".",
                profile.Theme,
                SuggestionFamily.Genre,
                genre.Value,
                ["Genre: " + genre.Key]));
        }
    }

    private static void AddFilmChannel(List<Candidate> candidates, IReadOnlyList<BaseItem> pool, SuggestionOptions options)
    {
        if (!options.Wants(SuggestionFamily.Film))
        {
            return;
        }

        var films = pool.OfType<Movie>().Where(movie => (movie.RunTimeTicks ?? 0) > 0).Cast<BaseItem>().ToList();
        if (films.Count < options.MinSources)
        {
            return;
        }

        candidates.Add(new Candidate(
            "Filmkanal",
            "Nur Filme, keine Folgen - ein durchlaufendes Programm aus deiner Filmbibliothek.",
            "Kino",
            SuggestionFamily.Film,
            films,
            ["Nur Filme"]));
    }

    /// <summary>
    /// Costs a candidate and turns it into a proposal, or refuses it.
    /// </summary>
    /// <remarks>
    /// Where the two new rules actually bite. The pool is first cut to the band it is mostly
    /// about - that is the coherence rule, and it runs even when no band was asked for. Then
    /// sources are taken while they fit the size the owner allowed, skipping any single source
    /// too large for the remaining budget rather than stopping at it: one enormous series must
    /// not be able to hide five that would have fitted.
    /// </remarks>
    /// <returns>The proposal, or null when what is left is not a channel.</returns>
    private static ChannelSuggestionDto? Compose(
        Candidate candidate,
        SuggestionOptions options,
        Func<Series, int> episodeCount,
        IReadOnlyCollection<string> libraries,
        Func<IReadOnlyList<BaseItem>, BaseItem, IReadOnlyList<BaseItem>>? cohesion)
    {
        var band = options.Audience == AudienceBand.Unknown
            ? SuggestionAudience.Dominant(candidate.Pool)
            : options.Audience;

        var coherent = band == AudienceBand.Unknown
            ? candidate.Pool
            : candidate.Pool.Where(item => SuggestionAudience.Fits(SuggestionAudience.Of(item), band)).ToList();

        /*
            Then the second cut, and the one the owner asked for after a DreamWorks channel came
            out holding Catch Me If You Can beside Kung Fu Panda. Sharing a studio is not being
            the same kind of thing - and the studio is often not even the main one on the film.
            So the pool is trimmed again, this time to the titles that resemble the one it is
            mostly about. Animation stays with animation.

            The anchor is the title most representative of the pool: the one sharing the most
            genres with the rest. Getting it wrong is survivable, because everything is measured
            against it and the pool's own majority decides who that is.
        */
        var anchor = Anchor(coherent);
        if (cohesion != null && anchor != null && coherent.Count > options.MinSources)
        {
            var kept = cohesion(coherent, anchor);
            if (kept.Count >= options.MinSources)
            {
                coherent = kept.ToList();
            }
        }

        /*
            The film night is decided first, because its films are then kept out of the channel's
            own sources.

            It used to be built from the sources themselves, which put the same films on twice -
            once as the channel's content and again as its Saturday block. Reserving them is the
            useful half of the fix: the channel runs its series, and the films it would otherwise
            have padded itself with become the evening that is worth sitting down for. A channel
            that is films already never gets one at all.
        */
        var reserved = new List<BaseItem>();
        if (options.WantsFilmNight(candidate.Family))
        {
            var films = coherent
                .OfType<Movie>()
                .Where(movie => (movie.RunTimeTicks ?? 0) > 0)
                .OrderByDescending(movie => movie.CommunityRating ?? 0)
                .ThenBy(movie => movie.SortName, StringComparer.OrdinalIgnoreCase)
                .Take(options.MaxSources)
                .Cast<BaseItem>()
                .ToList();

            // Only worth reserving when what is left is still a channel on its own.
            var remaining = coherent.Count - films.Count;
            if (films.Count >= options.MinSources && remaining >= options.MinSources)
            {
                reserved = films;
            }
        }

        var reservedIds = reserved.Select(item => item.Id).ToHashSet();
        var ranked = coherent
            .Where(item => !reservedIds.Contains(item.Id))
            .OrderByDescending(item => item is Series)
            .ThenByDescending(item => item.CommunityRating ?? 0)
            .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The same count the size cap is measured in, so the preview and the limit agree.
        int Titles(BaseItem item) => item is Series show ? Math.Max(episodeCount(show), 1) : 1;

        var budget = Math.Max(options.MaxTitles, options.MinSources);
        var chosen = new List<BaseItem>();
        var titles = 0;
        foreach (var item in ranked)
        {
            if (chosen.Count >= options.MaxSources)
            {
                break;
            }

            var cost = item is Series show ? Math.Max(episodeCount(show), 1) : 1;
            if (titles + cost > budget)
            {
                continue;
            }

            chosen.Add(item);
            titles += cost;
        }

        if (chosen.Count < options.MinSources)
        {
            return null;
        }

        var suggestion = new ChannelSuggestionDto
        {
            Name = candidate.Name,
            Description = candidate.Description,
            Theme = candidate.Theme,
            Sources = Sources(chosen, Titles),
            EpisodesPerBlock = 1,
            Order = nameof(PlayOrder.WeightedShuffle),
            RandomizeEpisodes = options.RandomizeEpisodes && chosen.Any(item => item is Series),
            Trailers = options.Trailers ? nameof(TrailerMode.Preview) : nameof(TrailerMode.Off),
            TrailerEveryPrograms = 3,
            TrailerLookahead = 3,
            TrailersInGaps = true,
            Artwork = new SuggestedArtworkDto { ItemId = chosen[0].Id, ItemName = chosen[0].Name ?? string.Empty },
            Reason = new SuggestionReasonDto
            {
                Family = candidate.Family,
                Audience = SuggestionAudience.Words(band),
                Because = candidate.Because.ToList(),
                Libraries = libraries.ToList(),
                SourceCount = chosen.Count,
                EstimatedTitles = titles,
                SizeLimit = budget
            }
        };

        suggestion.Features.Add("Gewichtet zufällig");
        if (suggestion.RandomizeEpisodes)
        {
            suggestion.Features.Add("Serienfolgen mischen");
        }

        if (options.Trailers)
        {
            suggestion.Features.Add("Trailer-Vorschau");
        }

        /*
            The film night is a second programme, so it needs its own films.

            It used to be built from the channel's own sources, which put the same films on twice
            - once as the channel's content and again as its Saturday block. A film channel gets
            none at all now, for the same reason said plainly: a block of films inside a channel
            that is already films is the same evening twice.
        */
        var roomForFilmNight = options.InsistsOnFilmNight || titles + reserved.Count <= budget;
        if (reserved.Count >= options.MinSources && roomForFilmNight)
        {
            suggestion.MovieNight = new SuggestedProgramBlockDto
            {
                Name = "Filmabend",
                StartMinutes = (20 * 60) + 15,
                Days = new List<string> { nameof(DayOfWeek.Saturday) },
                Sources = Sources(reserved, Titles),
                EpisodesPerBlock = 1,
                Order = nameof(PlayOrder.WeightedShuffle),
                RandomizeEpisodes = options.RandomizeEpisodes,
                AdvanceOnePerWeek = true,
                FitToContent = true,
                ShiftToAvoidLeadingGap = true,
                TrailerEnabled = options.Trailers,
                TrailerProgramsBefore = 3
            };
            suggestion.Features.Add("Filmabend · Sa 20:15");
        }

        return suggestion;
    }

    /// <summary>
    /// The title a pool is most about: the one sharing the most genres with the rest of it.
    /// </summary>
    /// <remarks>
    /// Deliberately not the highest rated or the newest. The question being asked is "what kind
    /// of thing is this pool", and the answer is whatever most of it looks like - a pool of six
    /// animated films and one thriller is about animation whichever of them scored best.
    /// </remarks>
    /// <param name="pool">The pool.</param>
    /// <returns>The anchor, or null when the pool is empty.</returns>
    private static BaseItem? Anchor(IReadOnlyList<BaseItem> pool)
    {
        BaseItem? best = null;
        var bestScore = -1;

        foreach (var item in pool)
        {
            var genres = item.Genres ?? Array.Empty<string>();
            var score = pool
                .Where(other => other.Id != item.Id)
                .Sum(other => (other.Genres ?? Array.Empty<string>())
                    .Count(genre => genres.Contains(genre, StringComparer.OrdinalIgnoreCase)));

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return best;
    }

    private static List<SuggestedSourceDto> Sources(IReadOnlyList<BaseItem> items, Func<BaseItem,int>? titles = null)
    {
        var baseWeight = 100 / items.Count;
        var remainder = 100 % items.Count;
        return items.Select((item, index) => new SuggestedSourceDto
        {
            Type = item is Series ? nameof(ChannelSourceType.Series) : nameof(ChannelSourceType.Movie),
            ItemId = item.Id,
            Name = item.Name ?? string.Empty,
            Probability = baseWeight + (index < remainder ? 1 : 0),
            Year = item.ProductionYear,
            Genres = (item.Genres ?? Array.Empty<string>()).Take(3).ToList(),
            Titles = titles?.Invoke(item) ?? 1
        }).ToList();
    }

    private static bool HasStudio(BaseItem item, IEnumerable<string> studios) =>
        (item.Studios ?? Array.Empty<string>()).Any(carried =>
            studios.Any(wanted => carried.Contains(wanted, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<BaseItem> WithGenre(IEnumerable<BaseItem> items, params string[] terms) =>
        items.Where(item => (item.Genres ?? Array.Empty<string>()).Any(genre =>
            terms.Any(term => genre.Contains(term, StringComparison.OrdinalIgnoreCase))));

    private static string Capitalise(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static Dictionary<string, List<BaseItem>> ByGenre(IEnumerable<BaseItem> items)
    {
        var grouped = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var genre in item.Genres ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(genre))
                {
                    continue;
                }

                if (!grouped.TryGetValue(genre, out var titles))
                {
                    grouped[genre] = titles = new List<BaseItem>();
                }

                titles.Add(item);
            }
        }

        return grouped;
    }

    private static (string Name, string Theme) GenreProfile(string genre) => genre.ToLowerInvariant() switch
    {
        "action" => ("Action Arena", "Action & Abenteuer"),
        "adventure" or "abenteuer" => ("Abenteuerzeit", "Action & Abenteuer"),
        "comedy" or "komödie" => ("Comedy & Chaos", "Comedy"),
        "crime" or "krimi" => ("Krimi nach Acht", "Krimi"),
        "documentary" or "dokumentation" => ("Wissen & Welt", "Dokumentation"),
        "animation" => ("Animationswelt", "Animation"),
        "science fiction" or "sci-fi" => ("Zukunftskino", "Science-Fiction"),
        "horror" => ("Nachtkino", "Horror"),
        "drama" => ("Große Geschichten", "Drama"),
        "family" or "familie" => ("Familienkino", "Familie"),
        _ => (genre + "-TV", genre)
    };
}
