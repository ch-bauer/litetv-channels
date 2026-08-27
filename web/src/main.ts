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

const app = mount(App, { target: host() });

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
