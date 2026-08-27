/*
 * The entry. Named `litetv.js` in the build because `configPage.html` points a script tag at it;
 * everything else it pulls in is content-hashed.
 *
 * Mounts into `#litetv-app`. The element is made by whatever hosts the app - the plugin's
 * configuration page in a real dashboard, or `index.html` under `vite dev` - so the app itself
 * has no opinion about the page around it beyond needing that one node.
 */
import { mount } from 'svelte';
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

export default mount(App, { target: host() });
