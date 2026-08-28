/*
 * The entry. Named `litetv.js` in the build because `configPage.html` points a script tag at it;
 * everything else it pulls in is content-hashed.
 *
 * Mounts into `#litetv-app`. The element is made by whatever hosts the app - the plugin's
 * configuration page in a real dashboard, or `index.html` under `vite dev` - so the app itself
 * has no opinion about the page around it beyond needing that one node.
 */
import { mount, unmount } from 'svelte';
import App from './App.svelte';

/** The app is unusable below this, so a bad measurement lands here rather than at nothing. */
const FLOOR = 460;

/*
 * What is left of the dashboard's bottom padding.
 *
 * Not nothing: an app welded to the bottom edge of the window looks like it has been cut off
 * rather than like it ends. This is the smallest gap that still reads as a margin.
 */
const GAP_BELOW = 10;

function host(): HTMLElement {
    const existing = document.getElementById('litetv-app');
    if (existing) { return existing; }
    // Injected into a dashboard mid-session there may be no host yet; make one rather than
    // failing silently, which is the failure mode this project keeps finding.
    const made = document.createElement('div');
    made.id = 'litetv-app';
    document.body.appendChild(made);
    return made;
}

/*
 * The dashboard's own bottom padding, taken for the duration.
 *
 * A plugin page is drawn inside `.page > .content-primary`, and both carry a bottom padding
 * meant for a page that flows: text that should not run into the bottom of the window. This
 * app is not that. It is a fixed-height thing that owns its screen and scrolls inside itself,
 * so most of that padding is a band of dead ground underneath it - which is what "a big empty
 * space at the bottom of the config page" has been every time it has been reported. A little
 * of it is kept, because an app flush against the window edge reads as cut off.
 *
 * The dashboard sets it with `!important`, so changing it needs the same weight. It is given
 * back on unmount, because the dashboard keeps these elements and draws other pages into them.
 */
function takeBottomPadding(node: HTMLElement): () => void {
    const containers = [node.closest('.content-primary'), node.closest('.page')]
        .filter((element): element is HTMLElement => element instanceof HTMLElement);

    // Nested, so a gap on each would be two gaps. The inner one carries it; the outer gives
    // up its padding entirely.
    const restore = containers.map((element, index) => {
        const had = element.style.getPropertyValue('padding-bottom');
        const priority = element.style.getPropertyPriority('padding-bottom');
        element.style.setProperty('padding-bottom', (index === 0 ? GAP_BELOW : 0) + 'px', 'important');
        return () => {
            if (had) { element.style.setProperty('padding-bottom', had, priority); }
            else { element.style.removeProperty('padding-bottom'); }
        };
    });

    return () => restore.forEach((undo) => undo());
}

/*
 * How tall the app is.
 *
 * Three goes at this have been wrong, each in a way the one before it could not see:
 *
 *   1. `innerHeight - top - 16` GUESSED at the room kept below the app. When the guess was
 *      short the page grew a scroll bar with nothing under it to scroll to.
 *   2. Subtracting however much the document overhung the window fixed that and brought the
 *      empty band back, because anything else on the page that overflowed took height off
 *      this app, which had nothing to do with it.
 *   3. "Collapse the app; whatever the document is then is everything that is NOT this app"
 *      is false on a real dashboard, and quietly so: `.page` is pinned to the full window
 *      height whatever the app does, so collapsing it does not shrink the document at all.
 *      The sum then always came to zero and the app always landed on its floor - a 460px app
 *      in a 1190px window, measured on the test server.
 *
 * So it is measured differentially, which needs no belief about the page at all. Collapse the
 * app and note how much the document overhangs the window: that overhang is somebody else's
 * and must not be charged here. Then offer the app the whole window below its own top, and see
 * how much the overhang grew: that much, and only that much, is this app's doing. Take it off.
 */
function fit(node: HTMLElement): () => void {
    const overhang = (): number => Math.max(0, document.documentElement.scrollHeight - window.innerHeight);

    const measure = (): void => {
        // Reading scrollHeight forces the layout, so each number below is of the page as it
        // stands after the line above it.
        node.style.setProperty('--lt-app-height', '0px');
        const foreign = overhang();

        // Clamped at zero: scrolled down, the top is negative, and subtracting it would make
        // the app grow every time it was measured.
        const top = Math.max(0, node.getBoundingClientRect().top);

        const candidate = Math.max(FLOOR, window.innerHeight - top);
        node.style.setProperty('--lt-app-height', candidate + 'px');
        const mine = Math.max(0, overhang() - foreign);

        node.style.setProperty('--lt-app-height', Math.max(FLOOR, candidate - mine) + 'px');
    };

    measure();
    // The dashboard animates a page in, so the first measurement is of a page still moving.
    const settled = setTimeout(measure, 300);
    window.addEventListener('resize', measure);

    return () => {
        clearTimeout(settled);
        window.removeEventListener('resize', measure);
    };
}

const target = host();
const giveBackPadding = takeBottomPadding(target);
const stopFitting = fit(target);

const app = mount(App, { target });

/*
 * The host page takes the app down again when the view is hidden, and it has to be Svelte that
 * does it: clearing the host node would leave every effect and listener in this module running
 * against detached DOM. The dashboard keeps a configuration page's element alive between visits,
 * so this is a real teardown and not a formality - and the page it hands back has to be the
 * page it lent us.
 */
declare global {
    interface Window { __litetvUnmount?: () => void; }
}
window.__litetvUnmount = () => {
    stopFitting();
    giveBackPadding();
    void unmount(app);
};

export default app;
