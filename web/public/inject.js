/*
 * Puts the app being worked on into a REAL Jellyfin dashboard, live.
 *
 * The old page had a harness that served it with a stubbed Jellyfin around it, and the owner's
 * verdict on that was blunt and correct: it is not the dashboard. It has no theme, no emby
 * component upgrade, no fixed header and no width cap - which is exactly where two releases went
 * visibly wrong while every test passed. So there is no harness any more. This runs the app
 * inside the dashboard itself, against the real server and the real data.
 *
 * With `npm run dev` running, open the LiteTV configuration page and paste into the console:
 *
 *     await import('http://localhost:8123/LiteTv/Web/inject.js');
 *
 * Vite's HMR then keeps it up to date: save a file here and the page updates itself. Reload the
 * dashboard to get the installed version back.
 */
(async () => {
    // Vite binds IPv6 by default, so `localhost` resolves and a literal 127.0.0.1 does not.
    // The path carries the base the build uses, which the dev server also serves under.
    const ROOT = 'http://localhost:8123/LiteTv/Web';

    if (!window.ApiClient) {
        console.error('[litetv] No ApiClient on this page - open the Jellyfin dashboard first.');
        return;
    }

    // Hide whatever the installed page has drawn, rather than removing it: reloading brings it
    // back, and nothing here should be able to damage the page it is borrowing.
    const page = document.querySelector('#litetvConfigPage, .page:not(.hide)') || document.body;
    for (const child of Array.from(page.children)) {
        if (child.id !== 'litetv-app') { child.style.display = 'none'; }
    }

    let host = document.getElementById('litetv-app');
    if (!host) {
        host = document.createElement('div');
        host.id = 'litetv-app';
        page.appendChild(host);
    }
    host.innerHTML = '';

    // Vite's client first, so hot reloading works; then the app itself.
    await import(ROOT + '/@vite/client');
    await import(ROOT + '/src/main.ts');

    console.info('[litetv] injected from ' + ROOT + ' - edits reload themselves.');
})();
