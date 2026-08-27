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
 * How tall the app is.
 *
 * It used to be `82vh` with a 720px floor, which is a guess about a page this app does not own:
 * too short and the dashboard shows a band of empty ground under it - which is what "a big
 * empty space at the bottom of the config page" was - and too tall and the whole dashboard
 * scrolls past a page that already scrolls inside itself.
 *
 * So it is measured: from wherever the app actually starts, down to the bottom of the window.
 * The dashboard animates a page in, so it is measured again once that has settled, and on every
 * resize after.
 */
function fit(node: HTMLElement): void {
    const measure = (): void => {
        // Clamped at zero: scrolled down, the top is negative, and subtracting it would make the
        // app grow every time it was measured.
        const top = Math.max(0, node.getBoundingClientRect().top);
        const height = Math.max(460, window.innerHeight - top - 16);
        node.style.setProperty('--lt-app-height', height + 'px');
    };

    measure();
    setTimeout(measure, 300);
    window.addEventListener('resize', measure);
}

const target = host();
fit(target);

const app = mount(App, { target });

/*
 * The host page takes the app down again when the view is hidden, and it has to be Svelte that
 * does it: clearing the host node would leave every effect and listener in this module running
 * against detached DOM. The dashboard keeps a configuration page's element alive between visits,
 * so this is a real teardown and not a formality.
 */
declare global {
    interface Window { __litetvUnmount?: () => void; }
}
window.__litetvUnmount = () => { void unmount(app); };

export default app;
