/*
 * Puts the configuration page you are editing into a REAL Jellyfin dashboard.
 *
 * The harness in serve.js is not the dashboard and does not look like it: no emby-* component
 * upgrade, no theme, no fixed header, no form width cap. Every fault that has shipped so far
 * lived in exactly that gap. This closes it without cutting a release for each attempt.
 *
 * Use it from the browser console (or a browser-automation eval) while the LiteTV configuration
 * page is open in the dashboard:
 *
 *     await fetch('http://127.0.0.1:8123/inject.js').then(r => r.text()).then(eval);
 *     await __ltvInject();
 *
 * It fetches src/.../configPage.html from the harness server, swaps its body into the page the
 * dashboard has already opened, and re-runs its scripts - so what you are looking at is your
 * working copy inside Jellyfin's own chrome, theme and components.
 *
 * Reload the dashboard page to get the installed version back.
 */
(function () {
    'use strict';

    var SOURCE = 'http://127.0.0.1:8123/page';

    /**
     * The dashboard's own container for a plugin configuration page. Jellyfin keeps only the
     * body of the page, so this is the element whose children are ours.
     */
    function host() {
        var mine = document.querySelector('#LiteTvConfigForm');
        if (mine) {
            // Walk up to the page div the dashboard created, not our own markup.
            var page = mine.closest('.page,[data-role="page"],.pageContainer');
            if (page) { return page; }
        }
        return document.querySelector('.page:not(.hide)') || document.body;
    }

    /** innerHTML does not run scripts; they have to be made again to execute. */
    function runScripts(root) {
        var scripts = Array.prototype.slice.call(root.querySelectorAll('script'));
        scripts.forEach(function (old) {
            var fresh = document.createElement('script');
            Array.prototype.forEach.call(old.attributes, function (a) {
                fresh.setAttribute(a.name, a.value);
            });
            fresh.textContent = old.textContent;
            old.parentNode.replaceChild(fresh, old);
        });
        return scripts.length;
    }

    window.__ltvInject = async function () {
        var markup = await fetch(SOURCE, { cache: 'no-store' }).then(function (r) { return r.text(); });

        // Take the body only, the way Jellyfin does - a whole document carries a `page` class
        // the web client's CSS hides until its own view manager shows it.
        var parsed = new DOMParser().parseFromString(markup, 'text/html');
        var body = parsed.body;

        var target = host();
        target.innerHTML = '';
        while (body.firstChild) { target.appendChild(body.firstChild); }

        var count = runScripts(target);

        // The page's own loader runs off pageshow, which nothing fires for markup put here by
        // hand; it has a setTimeout fallback, guarded so the two cannot both run. Give it a
        // moment and then say what happened.
        await new Promise(function (r) { setTimeout(r, 1200); });

        return {
            injected: true,
            scripts: count,
            panes: document.querySelectorAll('.litetvPane').length,
            rail: document.querySelectorAll('#ChannelRail .ltvRailRow').length,
        };
    };

    return 'call await __ltvInject()';
})();
