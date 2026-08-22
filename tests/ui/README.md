# The configuration page's UI suite

Run this before committing or releasing anything that touches `configPage.html`.

    "C:/Program Files/Adobe/Adobe Creative Cloud Experience/libs/node.exe" tests/ui/serve.js

Then open <http://127.0.0.1:8123/> and evaluate:

    await window.__ltvChecks()

It answers `{ total, failed, failures, passes }`. Anything in `failures` is a fault in the page,
unless the check itself is wrong — in which case fix the check, because a check nobody trusts is
worse than no check.

## Why it exists

Two releases in a row shipped a visibly broken page while the build was clean and all 120 C#
tests passed:

- **v1.0.61.0** put the stylesheet in the `<head>`. Jellyfin injects only the body of a plugin
  configuration page, so eleven kilobytes of CSS never reached the browser and the whole layout
  collapsed into one plain column.
- **v1.0.62.0** fixed that, and the page was then squeezed into the dashboard's 803 px form cap
  and slid underneath its fixed 48 px header.

A C# test cannot see a page. This can.

## What it covers

The stylesheet actually applying · the document never scrolling · nothing overflowing sideways on
any of the five channel screens · the rail listing, filtering, counting and tagging · every tab
showing exactly one pane · the week drawing seven days, opening on the evening, zooming, and
switching to a single day · the (?) toggles · the settings preview and the plugin strip arriving
from the server · both destinations hiding the channel tabs · and the form posting on save.

## What it cannot cover

The harness is not the dashboard. It has no `emby-*` component upgrade (so inputs look unlabelled
there and will not be in Jellyfin), no themes, no fixed header, and no form width cap — which is
precisely where both bad releases went wrong. **A clean run here is the floor, not the ceiling:
open the page on a real server and look at it too.**

## The pieces

| file | what it is |
|---|---|
| `serve.js` | serves the real `configPage.html` with the stub and the checks injected |
| `stub.js` | a fake `ApiClient`/`Dashboard` and a sixteen-channel fixture |
| `checks.js` | the assertions; defines `window.__ltvChecks()` |
