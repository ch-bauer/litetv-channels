using Jellyfin.Plugin.LiteTv.Configuration;

namespace Jellyfin.Plugin.LiteTv.Core;

/// <summary>
/// The one place that answers "what is this channel airing, and when". Everything that
/// needs to know - the web guide, the published channel items, the Live TV service, the
/// session monitor - asks here, so they cannot disagree with each other about a schedule
/// they would otherwise each have resolved themselves.
/// </summary>
public sealed class ChannelGuide
{
    private readonly ChannelPlaylistBuilder _builder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGuide"/> class.
    /// </summary>
    /// <param name="builder">The playlist builder.</param>
    public ChannelGuide(ChannelPlaylistBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>
    /// Gets the channels that are on air, in configuration order.
    /// </summary>
    /// <returns>The enabled channels.</returns>
    public static IReadOnlyList<TvChannel> Channels()
        => Plugin.Instance?.Configuration.Channels.Where(c => c.Enabled).ToList() ?? new List<TvChannel>();

    /// <summary>
    /// Gets one channel by id, if it is on air.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <returns>The channel, or null when it is unknown or disabled.</returns>
    public static TvChannel? Channel(Guid channelId)
        => Channels().FirstOrDefault(c => c.Id == channelId);

    /// <summary>
    /// Gets what a channel is airing at one moment.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="utc">The moment; defaults to now.</param>
    /// <returns>The airing, or null when the channel has nothing to air at all.</returns>
    public Airing? NowOn(TvChannel channel, DateTime? utc = null)
    {
        var at = utc ?? DateTime.UtcNow;
        var schedule = _builder.GetSchedule(channel);
        if (schedule.IsSilent)
        {
            return null;
        }

        return Window(channel, at, at.AddMinutes(1)).FirstOrDefault();
    }

    /// <summary>
    /// Gets the program a channel is airing at one moment, looking past anything that is
    /// not a program. What the player needs is something to play; an interstitial or a dark
    /// stretch is not that, and the answer then is what comes next and when.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="utc">The moment; defaults to now.</param>
    /// <returns>The program on air, or null when none is.</returns>
    public Airing? ProgramOn(TvChannel channel, DateTime? utc = null)
    {
        var at = utc ?? DateTime.UtcNow;
        var airing = NowOn(channel, at);
        return airing?.Kind == AiringKind.Program ? airing : null;
    }

    /// <summary>
    /// Walks a channel's schedule over a window of time, with the gaps between programs
    /// filled in.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="fromUtc">The start of the window; the first airing may start before it.</param>
    /// <param name="toUtc">The end of the window.</param>
    /// <returns>The airings, in order.</returns>
    public IEnumerable<Airing> Window(TvChannel channel, DateTime fromUtc, DateTime toUtc)
    {
        var airings = _builder.GetSchedule(channel).Enumerate(fromUtc, toUtc);
        return channel.TrailersInGaps ? WithTrailers(airings) : airings;
    }

    /// <summary>
    /// Fills the time a slot leaves over with trailers for the program about to start -
    /// which is what the leftover time is for. A trailer only goes in if it fits whole:
    /// half a trailer cut off by the start of the film is worse than a moment of quiet.
    /// Whatever is left over stays an empty interstitial, which the web client can fill
    /// with a trailer the library only knows the address of rather than holds.
    /// </summary>
    private IEnumerable<Airing> WithTrailers(IEnumerable<Airing> airings)
    {
        foreach (var airing in airings)
        {
            if (airing.Kind != AiringKind.Interstitial || airing.NextProgram is null)
            {
                yield return airing;
                continue;
            }

            var cursor = airing.StartUtc;
            foreach (var trailer in _builder.TrailersFor(airing.NextProgram.ItemId))
            {
                var end = cursor + TimeSpan.FromTicks(trailer.RuntimeTicks);
                if (end > airing.EndUtc)
                {
                    break;
                }

                yield return airing with { Entry = trailer, StartUtc = cursor, EndUtc = end };
                cursor = end;
            }

            if (cursor < airing.EndUtc)
            {
                yield return airing with { StartUtc = cursor };
            }
        }
    }
}
