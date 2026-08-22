/*
 * Serves the real configuration page with a stubbed Jellyfin around it, so the UI suite in
 * checks.js can be run against it in a browser.
 *
 *   node tests/ui/serve.js
 *   → http://127.0.0.1:8123/    then evaluate window.__ltvChecks()
 *
 * There is no node on PATH on the machine this was written for; it lives at
 * "C:/Program Files/Adobe/Adobe Creative Cloud Experience/libs/node.exe".
 *
 * The harness cannot stand in for the dashboard: no emby-* component upgrade, no themes, no
 * fixed header, and no 803px form cap. Those are exactly where the two bad releases went wrong,
 * so a clean run here is the floor - open the page on a real server as well.
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

const here = __dirname;
const pagePath = path.join(here, '..', '..', 'src', 'Jellyfin.Plugin.LiteTv', 'Configuration', 'configPage.html');

// Roughly what the dashboard paints underneath, so the page is legible while being looked at.
const GROUND = `
<style>
  body { background: #111827; color: #d1d5db; font-family: Inter, 'Noto Sans', sans-serif; margin: 0; padding: 1.5em; }
  .paperList { background: rgba(255,255,255,.03); }
  input, select { background: rgba(255,255,255,.05); color: inherit; border: 1px solid rgba(128,128,128,.3);
                  border-radius: 4px; padding: .4em .6em; font-family: inherit; }
  .inputLabel { display: block; font-size: .75em; opacity: .6; }
</style>
<script src="stub.js"></script>`;

function harness() {
    const page = fs.readFileSync(pagePath, 'utf8');
    // The stub has to be in place before the page's own script runs.
    return page.replace('</head>', GROUND + '\n</head>')
        .replace('</body>', '<script src="checks.js"></script>\n</body>');
}

http.createServer((req, res) => {
    const file = req.url === '/' ? null : req.url.split('?')[0];
    try {
        // The harness is not the dashboard, and the owner was right that it does not look like
        // it either: no emby-* component upgrade, no theme, no fixed header, no form width cap.
        // /page serves the page raw and cross-origin so it can be injected into a *real*
        // dashboard and looked at there - see README, "Testing in the real dashboard".
        if (file === '/page') {
            res.writeHead(200, {
                'Content-Type': 'text/html; charset=utf-8',
                'Access-Control-Allow-Origin': '*',
                'Cache-Control': 'no-store',
            });
            return res.end(fs.readFileSync(pagePath, 'utf8'));
        }

        if (!file) {
            const body = harness();
            res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
            return res.end(body);
        }
        const served = fs.readFileSync(path.join(here, path.basename(file)));
        res.writeHead(200, {
            'Content-Type': 'text/javascript; charset=utf-8',
            'Access-Control-Allow-Origin': '*',
            'Cache-Control': 'no-store',
        });
        res.end(served);
    } catch (err) {
        res.writeHead(404);
        res.end('not here');
    }
}).listen(8123, () => console.log('LiteTV UI harness on http://127.0.0.1:8123/'));
