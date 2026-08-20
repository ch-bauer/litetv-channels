# LiteTV Channels for Jellyfin (Proof of Concept)

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
  cleaned up afterwards — while a program airs on a channel its watch state is simply
  never written, on every client, however playback was started.

## Clients

| Client | Experience |
| --- | --- |
| Native apps (Android TV, iOS, …) | Browse the "TV-Sender" library entry and play a channel to join it live. The channels are deliberately not published to Jellyfin's own **Live TV** section: that route hands a client a stream rather than a position in one, so it could only ever start the current program from its beginning — a worse way in to the same channels |
| Any client, driven remotely | Ask the server to start a channel on a device with `POST /LiteTv/Channels/{id}/PlayOn/{sessionId}` — playback starts at the live position and the schedule keeps being pushed |

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

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.LiteTv -c Release
```

## License

MIT
