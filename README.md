<div align="center">
  <img src="images/icon.png" alt="LiteTV Channels for Jellyfin" width="128" />
  <h1>LiteTV Channels for Jellyfin (WIP)</h1>
</div>

> [!CAUTION]
> **This is a proof of concept, written with AI.** It is purely for testing, and there are
> many items that are known to be incorrect or broken. It is not advisable to use this on a
> non-test server.
>
> For this reason it is offered as is, with **no guarantee of support, bug fixes, or
> troubleshooting**.
>
> **It is NOT recommended to fork or build on top of this plugin!**

Lightweight virtual TV channels for your Jellyfin library — **no transcoding, no tuner
emulation, no separate app**. A channel is just a deterministic schedule over your own
movies and series: tuning in starts normal direct playback of whatever is "on air"
right now, at exactly the right position. Like flipping to a TV channel, but everything
comes straight from your library.

## How it works

- A channel is an ordered, endlessly looping queue of movies, series (played in
  aired episode order, specials included) and collections.
- What's on *now* is pure math: wall clock vs. the channel's anchor time and the item
  runtimes. Everyone who tunes in sees the same moment — no state, no streams running
  while nobody watches.
- **Program blocks** give a part of the week its own lineup: the kids' programming
  until noon, the film on Saturday evening. A block owns a window of the day on the
  weekdays it applies to; whatever no block covers airs the channel's own program. A
  block picks up where it left off the last time it was on.
- **Fixed slot times** start programs on a grid (say every 30 minutes) instead of back
  to back, so a block can promise the film at 20:15. The time a slot leaves over is
  filled with trailers for what is coming up — from the library where it has them, and
  the library where it has them.
- Tuning in simply starts regular playback (direct play when the client supports the
  file) at the live offset. A small overlay offers "restart from the beginning".
- At the end of an episode an overlay counts down to the next scheduled program — or
  lets you keep binging the current series instead. Untouched, the schedule wins,
  like real TV.
- Channel viewing leaves **no traces on the account**: no Continue Watching entries,
  no resume points, no watched flags, no Next Up progression. Nothing is recorded and
  cleaned up afterwards — a channel plays under a Jellyfin account of the plugin's own,
  and the server records a playback against whoever the token belongs to. So the viewing
  lands somewhere harmless no matter which client played it, or how.

## Clients

**The channels are not published to Jellyfin at all** — not as a library entry, not to the
**Live TV** section. Both routes were tried and both are worse than nothing. A Live TV channel
hands a client a stream rather than a position in one, so it could only ever start the current
program from its beginning. A published channel item is stored once and never re-resolved, so
its entries go stale at every changeover and a client that cached the listing holds dead ids.
Either way the channel appears on clients that cannot really play it.

Instead the plugin serves the schedule as data — `/LiteTv/Channels`, `/LiteTv/Guide` — and a
client that knows what a channel is builds the queue itself. Today that means the
**Wholphin LiteTV fork (WIP)** on Android TV. Every other
client sees nothing, which is the intent: a channel should show up only where it works.

Before playing anything, a client asks `GET /LiteTv/PlaybackUser` for the account to play
under, and uses that token for playback info, the stream and progress reports — not for
browsing. **A client that cannot get those credentials must refuse to start the channel**,
or the whole schedule records against the viewer.

The web client had an injected UI of its own — a home-screen channel row, a 📺 guide, playback
overlays. It was removed to concentrate on the TV app; the history is in git if it is ever
wanted back.

## Installation

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/ch-bauer/litetv-channels/main/manifest.json`
2. Install **LiteTV Channels** from the catalog and restart Jellyfin.
3. Configure channels under Dashboard → Plugins → LiteTV Channels.

## Configuration

Per channel: name, on-air toggle, and the program — an ordered list of movies, series
and collections. Series expand to their episodes in aired order; specials are placed
where their metadata says they air (before an episode, after a season) and otherwise
follow the numbered seasons. The schedule anchor can be reset so the loop starts over
from the first entry.

Also per channel:

- **Play order** — in order, or shuffled. The shuffle is drawn once from the channel
  itself and never from the clock, so the guide and the player always agree and the
  order survives a restart.
- **Slot minutes** — start programs on a grid instead of back to back. A program longer
  than one slot takes as many whole slots as it needs.
- **Trailers in gaps** — fill the leftover time with trailers for what is coming up.
- **Program blocks** — any number, each with a start time, a duration (which may run
  past midnight), the weekdays it applies to, and its own content and play order.
  Overlapping blocks resolve to the first one configured.

And for the plugin as a whole: the **channel playback account**. Channel viewing is recorded
against this account rather than yours; it is created on first use, hidden from the login
screen, and stripped of everything but the ability to play. Two consequences worth knowing:
anyone who can reach the API can ask for its token, so on a server with several people and
restricted libraries this account should be restricted to match; and the same title watched
deliberately on a client that is playing a channel is not recorded either, because it is the
account that decides, not what is playing.

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.LiteTv -c Release
```

## License

MIT — see [LICENSE](LICENSE).
