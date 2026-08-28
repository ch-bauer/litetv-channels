using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiteTv.Trailers;

/// <summary>
/// What is in a YouTube playlist.
/// <para>
/// A channel has always played library items; an address appeared only inside a break. This is
/// what lets a playlist be a <b>source</b>, the way a collection is - so a channel can be a music
/// video channel, or one uploader, with nothing from the library in it at all.
/// </para>
/// <para>
/// A playlist is expanded <b>when a week is laid out</b>, never stored and never again. That is
/// the same rule the stored week already follows, and it is the one that makes sense here: a
/// playlist that gains a video should reach the channel the next time the week is written, not
/// silently change what a written-down schedule says is airing.
/// </para>
/// <para>
/// It reads YouTube's own <c>browse</c> endpoint rather than the Data API: the rest of this
/// plugin resolves trailers with no API key and no quota, and having one feature alone need a
/// key would be the thing that stops working.
/// </para>
/// </summary>
public sealed class YouTubePlaylist
{
    /// <summary>How many items are read at most, over all continuations.</summary>
    private const int Cap = 400;

    private const string BrowseEndpoint =
        "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubePlaylist> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubePlaylist"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public YouTubePlaylist(IHttpClientFactory httpClientFactory, ILogger<YouTubePlaylist> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>A playlist: its own name, and the videos in it.</summary>
    /// <param name="Title">What YouTube calls it; empty when it would not say.</param>
    /// <param name="Items">The videos, in playlist order.</param>
    public sealed record Playlist(string Title, IReadOnlyList<Item> Items);

    /// <summary>
    /// One video in a playlist.
    /// </summary>
    /// <param name="VideoId">The video's id.</param>
    /// <param name="Title">Its title, as the guide will show it.</param>
    /// <param name="Seconds">How long it runs, or zero when the page did not say.</param>
    public sealed record Item(string VideoId, string Title, int Seconds)
    {
        /// <summary>Gets a watchable address for this video.</summary>
        public string Url => "https://www.youtube.com/watch?v=" + VideoId;
    }

    /// <summary>
    /// The playlist id in an address, or null when there is not one.
    /// </summary>
    /// <param name="url">Any YouTube address.</param>
    /// <returns>The playlist id, or null.</returns>
    public static string? PlaylistId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();

        // A bare id, pasted on its own. Playlist ids begin PL, UU, LL, FL, RD or OLAK.
        if (!trimmed.Contains('/', StringComparison.Ordinal)
            && !trimmed.Contains('?', StringComparison.Ordinal)
            && LooksLikeId(trimmed))
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length == 2 && string.Equals(split[0], "list", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(split[1]);
                return LooksLikeId(value) ? value : null;
            }
        }

        return null;
    }

    private static bool LooksLikeId(string value)
    {
        // Long enough to be an id, and made only of what an id is made of. Deliberately loose:
        // YouTube has added prefixes before, and refusing a real playlist is worse than
        // attempting one that turns out not to exist - which simply answers with nothing.
        return value.Length >= 12
            && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    /// <summary>
    /// Reads a playlist.
    /// </summary>
    /// <param name="url">The playlist address, or a bare playlist id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What is in it, in playlist order; empty when it could not be read.</returns>
    public async Task<IReadOnlyList<Item>> ItemsAsync(string? url, CancellationToken cancellationToken)
        => (await ReadAsync(url, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// The playlist: what it is called, and what is in it.
    /// <para>
    /// The title is read because the alternative was inventing one. The page used to name a
    /// playlist source <c>"16 videos - &lt;first video's title&gt;"</c> - a description, not a
    /// name - and the schedule then carried that under every programme as though it were the
    /// series. The owner read the two lines together and reported the schedule as wrong, which
    /// is what a name that is not a name costs.
    /// </para>
    /// </summary>
    /// <param name="url">The playlist address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist.</returns>
    public async Task<Playlist> ReadAsync(string? url, CancellationToken cancellationToken)
    {
        var id = PlaylistId(url);
        if (id is null)
        {
            return new Playlist(string.Empty, Array.Empty<Item>());
        }

        var title = string.Empty;
        var items = new List<Item>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;

        try
        {
            using var http = _httpClientFactory.CreateClient();

            // Guarded rather than "while there is a continuation": a playlist that keeps
            // answering with a continuation and no new items would otherwise spin here forever.
            for (var page = 0; page < 12 && items.Count < Cap; page++)
            {
                var json = await BrowseAsync(http, id, continuation, cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    break;
                }

                using (json)
                {
                    // Only the first page carries it; a continuation is items and nothing else.
                    if (title.Length == 0)
                    {
                        title = TitleOf(json.RootElement) ?? string.Empty;
                    }

                    var before = items.Count;
                    continuation = Harvest(json.RootElement, items, seen);
                    if (items.Count == before || continuation is null)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Said out loud. A playlist that quietly comes back empty looks exactly like a
            // playlist with nothing in it, and this project has been bitten by that shape of
            // silence more than once.
            _logger.LogWarning(ex, "LiteTV could not read YouTube playlist {Playlist}.", id);
            return new Playlist(title, items);
        }

        _logger.LogInformation(
            "LiteTV read {Count} videos from YouTube playlist {Playlist}.",
            items.Count,
            id);

        return new Playlist(title, items);
    }

    /// <summary>
    /// What the playlist is called.
    /// <para>
    /// Looked for in the three places YouTube has put it, newest first, rather than in the one
    /// that happens to work today - the browse response has been reshaped repeatedly, which is
    /// why <see cref="Harvest"/> walks rather than indexes.
    /// </para>
    /// </summary>
    /// <param name="root">The browse response.</param>
    /// <returns>The title, or null.</returns>
    private static string? TitleOf(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("playlistMetadataRenderer", out var meta)
            && Text(meta, "title") is { Length: > 0 } fromMetadata)
        {
            return fromMetadata;
        }

        if (root.TryGetProperty("header", out var header))
        {
            if (header.TryGetProperty("playlistHeaderRenderer", out var old)
                && Text(old, "title") is { Length: > 0 } fromHeader)
            {
                return fromHeader;
            }

            if (header.TryGetProperty("pageHeaderRenderer", out var page)
                && Text(page, "pageTitle") is { Length: > 0 } fromPage)
            {
                return fromPage;
            }
        }

        return null;
    }

    private static async Task<JsonDocument?> BrowseAsync(
        HttpClient http,
        string playlistId,
        string? continuation,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["context"] = new Dictionary<string, object?>
            {
                ["client"] = new Dictionary<string, object?>
                {
                    ["clientName"] = "WEB",
                    ["clientVersion"] = "2.20240722.01.00",

                    // The language the SCHEDULE ends up in: YouTube answers with the title
                    // localised for `hl`, so this is what decides whether a German household
                    // reads German programme names. See YouTubeLocale.
                    ["hl"] = YouTubeLocale.Language(),
                    ["gl"] = YouTubeLocale.Region()
                }
            }
        };

        if (continuation is null)
        {
            // VL + the playlist id is how a playlist is addressed as a browsable thing.
            body["browseId"] = "VL" + playlistId;
        }
        else
        {
            body["continuation"] = continuation;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BrowseEndpoint)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("User-Agent", YouTubeStreamResolver.UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("Referer", YouTubeStreamResolver.Referer);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20240722.01.00");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await JsonDocument
            .ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                default,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls every <c>playlistVideoRenderer</c> out of a response, wherever it sits.
    /// <para>
    /// Walked rather than addressed by path on purpose: the shape around these objects has
    /// changed repeatedly - two-column, tabs, section lists, continuations - while the renderer
    /// itself has not. A walk survives a reshuffle; a path does not.
    /// </para>
    /// </summary>
    /// <param name="element">The response root.</param>
    /// <param name="items">Where to put what is found.</param>
    /// <param name="seen">Video ids already taken, so a continuation cannot double up.</param>
    /// <returns>The next continuation token, or null.</returns>
    internal static string? Harvest(JsonElement element, List<Item> items, HashSet<string> seen)
    {
        string? continuation = null;
        Walk(element, items, seen, ref continuation);
        return continuation;
    }

    private static void Walk(JsonElement element, List<Item> items, HashSet<string> seen, ref string? continuation)
    {
        if (items.Count >= Cap)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("playlistVideoRenderer", out var video))
                {
                    Take(video, items, seen);
                    return;
                }

                // What YouTube actually sends now. Measured 27 Aug 2026 against a real playlist:
                // the WEB browse response carries no `playlistVideoRenderer` at all any more -
                // every entry is a `lockupViewModel`. The old shape is kept because a walk costs
                // nothing and this is the third shape these entries have had.
                if (element.TryGetProperty("lockupViewModel", out var lockup))
                {
                    TakeLockup(lockup, items, seen);
                    return;
                }

                if (element.TryGetProperty("continuationCommand", out var command)
                    && command.TryGetProperty("token", out var token)
                    && token.GetString() is { Length: > 0 } text)
                {
                    continuation = text;
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, items, seen, ref continuation);
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, items, seen, ref continuation);
                }

                break;
        }
    }

    private static void Take(JsonElement video, List<Item> items, HashSet<string> seen)
    {
        if (!video.TryGetProperty("videoId", out var idElement)
            || idElement.GetString() is not { Length: > 0 } videoId
            || !seen.Add(videoId))
        {
            return;
        }

        var title = Text(video, "title") ?? videoId;
        var seconds = Seconds(video);

        items.Add(new Item(videoId, title, seconds));
    }

    /// <summary>
    /// One entry in the shape YouTube uses today.
    /// <para>
    /// A lockup keeps the same three facts somewhere else: the video id is <c>contentId</c>, the
    /// title is <c>metadata.lockupMetadataViewModel.title.content</c>, and the length is only
    /// ever the badge drawn over the thumbnail - "9:52" - so it is read the same way a visible
    /// duration always was.
    /// </para>
    /// <para>
    /// The content type is checked, because a playlist page also lockups things that are not
    /// videos, and scheduling a channel or a playlist as if it were a programme is worse than
    /// skipping it.
    /// </para>
    /// </summary>
    /// <param name="lockup">The lockup.</param>
    /// <param name="items">Where to put what is found.</param>
    /// <param name="seen">Video ids already taken.</param>
    private static void TakeLockup(JsonElement lockup, List<Item> items, HashSet<string> seen)
    {
        if (lockup.TryGetProperty("contentType", out var type)
            && type.GetString() is { Length: > 0 } kind
            && !kind.Contains("VIDEO", StringComparison.Ordinal))
        {
            return;
        }

        if (!lockup.TryGetProperty("contentId", out var idElement)
            || idElement.GetString() is not { Length: > 0 } videoId
            || !seen.Add(videoId))
        {
            return;
        }

        var title = videoId;
        if (lockup.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("lockupMetadataViewModel", out var model)
            && model.TryGetProperty("title", out var titleNode)
            && titleNode.TryGetProperty("content", out var content)
            && content.GetString() is { Length: > 0 } written)
        {
            title = written;
        }

        items.Add(new Item(videoId, title, LockupSeconds(lockup)));
    }

    /// <summary>
    /// The length badge on a lockup's thumbnail, in seconds, or zero when there is not one -
    /// which is what a live entry looks like.
    /// </summary>
    /// <param name="lockup">The lockup.</param>
    /// <returns>The length in seconds.</returns>
    private static int LockupSeconds(JsonElement lockup)
    {
        var text = FirstBadgeText(lockup);
        return text is null ? 0 : Clock(text);
    }

    private static string? FirstBadgeText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("thumbnailBadgeViewModel", out var badge)
                    && badge.TryGetProperty("text", out var badgeText)
                    && badgeText.GetString() is { Length: > 0 } written
                    // A badge also carries "New", "4K" and the like; a length has a colon in it.
                    && written.Contains(':', StringComparison.Ordinal))
                {
                    return written;
                }

                foreach (var property in element.EnumerateObject())
                {
                    var found = FirstBadgeText(property.Value);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    var found = FirstBadgeText(child);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    /// <summary>YouTube writes text as either a plain string, a "simpleText", or "runs".</summary>
    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString();
        }

        if (node.TryGetProperty("simpleText", out var simple))
        {
            return simple.GetString();
        }

        if (node.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(runs.EnumerateArray()
                .Select(r => r.TryGetProperty("text", out var t) ? t.GetString() : null));
        }

        return null;
    }

    /// <summary>
    /// How long the video runs. <c>lengthSeconds</c> when it is there; otherwise the visible
    /// "12:34", which is all a live or upcoming entry ever has.
    /// </summary>
    private static int Seconds(JsonElement video)
    {
        if (video.TryGetProperty("lengthSeconds", out var length)
            && int.TryParse(length.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        var text = Text(video, "lengthText");
        return text is null ? 0 : Clock(text);
    }

    /// <summary>
    /// A visible duration - "9:52", "1:04:11" - in seconds, or zero when it is not one.
    /// </summary>
    /// <param name="text">The written duration.</param>
    /// <returns>The seconds.</returns>
    private static int Clock(string text)
    {
        var total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            total = (total * 60) + value;
        }

        return total;
    }
}
