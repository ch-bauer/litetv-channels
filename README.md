<div align="center">
  <img src="images/icon.png" alt="LiteTV Channels for Jellyfin" width="128" />
  <h1>LiteTV Channels for Jellyfin (WIP)</h1>
</div>

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
  in the web client from the linked (usually YouTube) ones where it does not.
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
| Web browser / apps embedding the web UI | Full: home-screen channel row, 📺 guide as a time grid, overlays, autoplay, trailers between programs. A channel switched on from the "TV-Sender" library entry is taken over by the same UI, so it plays the program itself — with its own artwork, plot and pause screen — rather than the bare channel entry |
| Native apps (Android TV, iOS, …) | Browse the "TV-Sender" library entry and play a channel to join it live. Optionally the channels also appear in Jellyfin's own **Live TV** section with the full schedule in the built-in guide — switching on there starts the current program from its beginning rather than joining it live, because Live TV hands a client a stream and not a position in one |
| Any client, driven from a browser | Open the guide on any browser (e.g. your phone) and "Auf Gerät…" — the server starts channel playback on the device at the live position and keeps pushing the next program |

## Installation

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/ch-bauer/jellyfin-plugin-litetv/main/manifest.json`
2. Install **LiteTV Channels** from the catalog and restart Jellyfin.
3. Configure channels under Dashboard → Plugins → LiteTV Channels.
4. Hard-refresh the browser once (Ctrl+F5).

Installing the [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
is recommended (LiteTV integrates with it like Intro Skipper does); without it, LiteTV
falls back to injecting its script at request time via middleware.

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

And for the web client as a whole: the channel row and the 📺 guide button can each be
turned off, and Jellyfin's own **"Live TV" and "On Now" home rows can be hidden** — they
list the same channels a second time once the channel row is there. The Live TV section
itself stays where it is; only the home screen changes.

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.LiteTv -c Release
```

## License

MIT
