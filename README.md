# LiteTV Channels for Jellyfin (Proof of Concept)

Lightweight virtual TV channels for your Jellyfin library — **no transcoding, no tuner
emulation, no separate app**. A channel is just a deterministic schedule over your own
movies and series: tuning in starts normal direct playback of whatever is "on air"
right now, at exactly the right position. Like flipping to a TV channel, but everything
comes straight from your library.

## How it works

- A channel is an ordered, endlessly looping queue of movies, series (played in
  chronological episode order) and collections.
- What's on *now* is pure math: wall clock vs. the channel's anchor time and the item
  runtimes. Everyone who tunes in sees the same moment — no state, no streams running
  while nobody watches.
- Tuning in simply starts regular playback (direct play when the client supports the
  file) at the live offset. A small overlay offers "restart from the beginning".
- At the end of an episode an overlay counts down to the next scheduled program — or
  lets you keep binging the current series instead. Untouched, the schedule wins,
  like real TV.
- Channel viewing leaves **no traces on the account**: no Continue Watching entries,
  no resume points, no watched flags, no Next Up progression.

## Clients

| Client | Experience |
| --- | --- |
| Web browser / apps embedding the web UI | Full: home-screen channel row, 📺 guide button, overlays, autoplay |
| Native apps (Android TV, iOS, …) | Basic: open the guide on any browser (e.g. your phone) and "Auf Gerät…" — the server starts channel playback on the device and keeps pushing the next program |

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
and collections. Series expand to their episodes in aired order (specials are skipped).
The schedule anchor can be reset so the loop starts over from the first entry.

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.LiteTv -c Release
```

## License

MIT
