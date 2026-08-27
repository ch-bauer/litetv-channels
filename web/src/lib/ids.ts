/*
 * New identifiers, in a page that is not always served over https.
 *
 * `crypto.randomUUID` is a secure-context API: on a dashboard reached at
 * `http://192.168.1.10:8096` - which is how most of these servers are reached - it is simply
 * **undefined**, and calling it throws. That throw happened inside "make me a channel", before
 * the screen was changed, so the button appeared to do nothing at all.
 *
 * `crypto.getRandomValues` has no such restriction, so the id is built from it and the server
 * gets the dashed form it parses either way.
 */
export function newId(): string {
    const c = globalThis.crypto;
    if (typeof c?.randomUUID === 'function') {
        return c.randomUUID();
    }

    const bytes = new Uint8Array(16);
    c.getRandomValues(bytes);
    // Version 4, variant 1: the bits a Guid parser checks.
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;

    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16)
        + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
}
