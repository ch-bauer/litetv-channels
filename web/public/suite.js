/*
 * The configuration app, tested where it actually runs.
 *
 * There is no harness. There was one, and the owner's verdict on it was blunt and correct: it is
 * not the dashboard - no theme, no emby components, no fixed header, no real server - which is
 * exactly where two releases went visibly wrong while every test passed. So this runs INSIDE a
 * real Jellyfin dashboard, against the real server, either against the installed page or against
 * the working copy put there by `inject.js`.
 *
 * With `npm run dev` running, open the LiteTV configuration page and paste into the console:
 *
 *     const s = await import('http://localhost:8123/LiteTv/Web/suite.js');
 *     await s.run();
 *
 * That works against the INSTALLED page as it stands, which is the run worth doing before a
 * release; add `await import('http://localhost:8123/LiteTv/Web/inject.js')` first to run it
 * against the working copy instead. The file is served by the dev server and deliberately not
 * embedded in the plugin - it writes to channels, and the bundle route is anonymous.
 *
 * `run()` prints a table and returns `{ passed, failed, checks }`.
 *
 * WHAT IT COVERS, and why these things:
 *
 *  - **The contract the server keeps.** Every fault that has cost a release here has been a body
 *    the server could not read - an enum name it had never heard of (1.0.82, every channel
 *    unsaveable), an empty string where a Guid belongs (1.0.94, no playlist could be added). Both
 *    are shaped alike and both are invisible to a type checker, because the two sides only meet
 *    over the wire. So the wire is what is checked: written, read back, and compared.
 *  - **The page as a person uses it.** Typing in the search box, clicking a result, selecting a
 *    row. Asserted through the DOM, because a function that returns the right array is not the
 *    same as a page that shows it.
 *
 * WHAT IT CANNOT SEE: anything about how it looks. Size, position, overflow, contrast, whether
 * the thing you want is on the screen at all - a green run is not evidence of any of that. Look
 * at it. That rule has not changed and this file does not replace it.
 *
 * A synthetic click is not a real one, either. The pointer sequence below was measured against
 * this dashboard with a real mouse - mousedown, focus, mouseup, click, in that order - so it
 * reproduces the order the browser actually uses, but a test that dispatches its own events can
 * still pass where a hand fails.
 */

const PLUGIN_ID = '13953c97-f5a0-4713-8d4c-96b5369e5791';
const NO_ITEM = '00000000-0000-0000-0000-000000000000';

/* A playlist that has outlived every other one used here. Overridable: `run({ playlist })`. */
const DEFAULT_PLAYLIST = 'https://www.youtube.com/playlist?list=PLrEnWoR732-BHrPp_Pm8_VleD68f9s14-';

/* ------------------------------------------------------------------ the small amount of scaffolding */

const checks = [];
let only = null;

async function check(group, name, fn) {
    if (only && group !== only) { return; }
    const started = performance.now();
    try {
        await fn();
        checks.push({ group, name, ok: true, ms: Math.round(performance.now() - started) });
    } catch (err) {
        const why = err && err.message ? err.message : String(err);
        // A check that cannot be ASKED here is not a check that failed. Only a few can say this
        // - the keyboard one, which needs the tab to be in front - and a red mark for that
        // teaches the next person to read a red run as normal, which is how a suite dies.
        checks.push({
            group,
            name,
            ok: why.startsWith('SKIPPED'),
            skipped: why.startsWith('SKIPPED'),
            ms: Math.round(performance.now() - started),
            why,
        });
    }
}

function is(actual, expected, what) {
    const a = JSON.stringify(actual);
    const e = JSON.stringify(expected);
    if (a !== e) { throw new Error((what ? what + ': ' : '') + 'expected ' + e + ', got ' + a); }
}

function ok(value, what) {
    if (!value) { throw new Error(what || 'expected something truthy'); }
}

/** Waits for a condition, and says what it was waiting for when it never comes. */
async function until(what, fn, timeout = 8000) {
    const stop = performance.now() + timeout;
    for (;;) {
        const value = await fn();
        if (value) { return value; }
        if (performance.now() > stop) { throw new Error('timed out waiting for ' + what); }
        await new Promise((r) => setTimeout(r, 60));
    }
}

/** The HTTP status of a call that is expected to fail, rather than an exception nobody can read. */
async function statusOf(promise) {
    try {
        await promise;
        return 200;
    } catch (err) {
        return err && typeof err.status === 'number' ? err.status : -1;
    }
}

const api = () => window.ApiClient;

/*
 * Two spellings of the same id.
 *
 * Jellyfin writes guids into a plugin's own answers WITHOUT dashes and everywhere else with
 * them, so `channel.Id === theIdWeAskedFor` is false for a channel that is plainly there. This
 * cost the first run of this file four checks that were all really one.
 */
const sameId = (a, b) => String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
const byId = (list, id) => list.find((c) => sameId(c.Id, id));

/**
 * A new id on a page that is not served over https.
 *
 * `crypto.randomUUID` is a secure-context API and is simply undefined on `http://192.168.x.x`,
 * which is how these servers are reached - the same trap `lib/ids.ts` exists for.
 */
function newId() {
    const c = globalThis.crypto;
    if (typeof c.randomUUID === 'function') { return c.randomUUID(); }
    const bytes = new Uint8Array(16);
    c.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
}

const post = (path, body) => api().fetch({
    url: api().getUrl(path),
    type: 'POST',
    data: JSON.stringify(body),
    contentType: 'application/json',
});

/* ------------------------------------------------------------------ the page, driven */

/**
 * A click, in the order the browser makes one.
 *
 * Measured in this dashboard: mousedown, then focus, then mouseup, then click. The order is the
 * whole point - the list under test decides what a click means by comparing the selection before
 * the press with the one after focus, so a test that fires `click` alone tests nothing.
 */
function clickLike(el) {
    const opts = { bubbles: true, cancelable: true, view: window };
    el.dispatchEvent(new MouseEvent('mousedown', opts));
    if (el.tabIndex >= 0 || el.tagName === 'BUTTON' || el.tagName === 'INPUT') { el.focus(); }
    el.dispatchEvent(new MouseEvent('mouseup', opts));
    el.dispatchEvent(new MouseEvent('click', opts));
}

/** Types into a bound input the way Svelte hears it. */
function typeInto(input, value) {
    input.focus();
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
}

const app = () => document.getElementById('litetv-app') || document.querySelector('.litetv') || document.body;
const $ = (sel) => app().querySelector(sel);
const $$ = (sel) => Array.from(app().querySelectorAll(sel));

/** The tab strip along the top, by the word on it. */
async function openTab(word) {
    const german = { Week: 'Woche', Content: 'Inhalt', Breaks: 'Pausen', Look: 'Aussehen', Settings: 'Einstellungen' };
    const button = $$('button').find((b) => [word, german[word]].includes(b.textContent.trim()));
    ok(button, 'no tab called ' + word);
    clickLike(button);
    await new Promise((r) => setTimeout(r, 120));
}

/** The channel rail's rows, and picking one by name. */
async function openChannel(name) {
    const row = $$('button, [role="option"], li').find((n) => n.textContent.includes(name));
    ok(row, 'no channel called ' + name + ' in the rail');
    clickLike(row);
    await new Promise((r) => setTimeout(r, 150));
}

const searchBox = () => $('input[type="search"]');
const resultRows = () => $$('.results .hit');
const sourceRows = () => $$('[data-source-row]');

function tagOf(row) {
    const tag = row.querySelector('.kind');
    return tag ? tag.textContent.trim() : '';
}

/* ------------------------------------------------------------------ the checks */

async function serverChecks(state) {
    await check('server', 'the plugin answers with its channels', async () => {
        const channels = await api().getJSON(api().getUrl('LiteTv/Definitions'));
        ok(Array.isArray(channels), 'Definitions did not answer with a list');
        ok(channels.length > 0, 'this server has no channels to test against');
        state.channels = channels;
    });

    await check('server', 'the guide says what is on', async () => {
        const guide = await api().getJSON(api().getUrl('LiteTv/Channels'));
        ok(Array.isArray(guide.Channels), 'no Channels in the guide payload');
        // Enabled ones only - a channel switched off is not on the air and must not be in it.
        is(guide.Channels.length, state.channels.filter((c) => c.Enabled).length, 'the guide and the definitions disagree on how many channels are on air');
    });

    /*
        A channel posted back exactly as it came must be accepted. This is the cheapest possible
        test and it is the one that would have caught 1.0.82, where a value the page wrote and the
        server could not read failed every save on the server.
    */
    await check('server', 'a channel survives a round trip unchanged', async () => {
        const one = state.channels[0];
        is(await statusOf(post('LiteTv/Definitions/' + one.Id, one)), 200, 'posting a channel back unchanged');
    });

    await check('server', 'a temporary channel can be made and read back', async () => {
        const made = {
            Id: state.tempId,
            Position: 0,
            Name: 'LiteTV suite - safe to delete',
            // Enabled, because the guide only knows enabled channels and the cycle is read from
            // the guide. It is deleted at the end of the run either way.
            Enabled: true,
            AnchorUtc: new Date().toISOString(),
            Sources: [],
            Adverts: [],
            ScheduleEdits: [],
            EpisodesPerBlock: 1,
            Order: 'Sequential',
            SlotMinutes: 0,
            TrailersInGaps: true,
            Trailers: 'Off',
            TrailerEveryPrograms: 3,
            TrailerLookahead: 3,
            TrailerTitles: [],
            Blocks: [],
            TrailerSlots: [],
            Artwork: {},
        };
        state.temp = made;
        is(await statusOf(post('LiteTv/Definitions/' + made.Id, made)), 200, 'creating the temporary channel');
        const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), made.Id);
        ok(back, 'the temporary channel was not there when read back');
    });

    await check('server', 'channel artwork survives upload, save and reload', async () => {
        // A real browser upload catches the failure mode that a JSON-only round trip cannot:
        // the picture is stored first, then the channel definition points at it.
        const png = new Uint8Array([
            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0,
        ]);
        const response = await fetch(api().getUrl('LiteTv/Artwork/' + state.tempId + '/banner'), {
            method: 'POST',
            headers: {
                'Authorization': 'MediaBrowser Token="' + api().accessToken() + '"',
                'X-Emby-Token': api().accessToken(),
            },
            body: new Blob([png], { type: 'image/png' }),
        });
        ok(response.ok, 'uploading channel artwork');
        const body = { ...state.temp, Artwork: { BannerUrl: '/LiteTv/Artwork/' + state.tempId + '/banner' } };
        is(await statusOf(post('LiteTv/Definitions/' + body.Id, body)), 200, 'saving artwork selection');
        const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), body.Id);
        is(back.Artwork.BannerUrl, body.Artwork.BannerUrl, 'the selected picture did not survive reload');
    });

    /*
        The 1.0.94 fault, both halves. `ChannelSource.ItemId` is a Guid on the server, so an empty
        string fails to bind and the whole save answers 400 before any of our code runs - which is
        why "add a playlist, press Save" broke with a number and no words. Both directions are
        asserted: the guid form saves, the empty string is still refused. A test that only checked
        the good one would go green again the day somebody re-broke it.
    */
    await check('server', 'a link source saves with the empty guid', async () => {
        const body = { ...state.temp, Sources: [{ Type: 'YouTube', ItemId: NO_ITEM, Name: 'a playlist', Url: state.playlist }] };
        is(await statusOf(post('LiteTv/Definitions/' + body.Id, body)), 200, 'saving a YouTube source');
        const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), body.Id);
        is(back.Sources.length, 1, 'the link was not stored');
        is(back.Sources[0].Type, 'YouTube', 'the source came back as something else');
        is(back.Sources[0].Url, state.playlist, 'the address did not survive');
    });

    await check('server', 'an empty string where a guid belongs is still refused', async () => {
        const body = { ...state.temp, Sources: [{ Type: 'YouTube', ItemId: '', Name: 'a playlist', Url: state.playlist }] };
        is(await statusOf(post('LiteTv/Definitions/' + body.Id, body)), 400, 'the empty string should not bind');
    });

    /*
        The enum on the wire. `Episode` was added in 1.0.94 and the page writes the NAME, so what
        matters is that the server reads that name back as the same member - the shape of the
        1.0.82 fault exactly.
    */
    await check('server', 'an Episode source survives a write and a read', async () => {
        const episode = await someEpisode();
        const body = { ...state.temp, Sources: [{ Type: 'Episode', ItemId: episode.Id, Name: episode.Name, Url: '' }] };
        is(await statusOf(post('LiteTv/Definitions/' + body.Id, body)), 200, 'saving an Episode source');
        const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), body.Id);
        is(back.Sources[0].Type, 'Episode', 'the episode came back as another kind');
    });

    await check('server', 'a channel of one episode lays a week out', async () => {
        is(await statusOf(post('LiteTv/Channels/' + state.tempId + '/Week/Generate', {})), 200, 'laying the week out');
        const week = await api().getJSON(api().getUrl('LiteTv/Channels/' + state.tempId + '/Week'));
        ok(week.Curated, 'the channel says nobody has laid a week out');
        ok((week.Airings || []).length > 0, 'the week came back with no airings');
        // "Programme", with the E: the enum member is `Program` and the DTO spells it out the
        // way the guide reads. Asserting the enum's own spelling here failed a week that was
        // perfectly correct.
        ok(week.Airings.some((a) => a.Kind === 'Programme'), 'the week has no programmes in it at all');
    });

    await check('server', 'the cycle is described in words', async () => {
        const said = await api().getJSON(api().getUrl('LiteTv/Channels/' + state.tempId + '/Cycle'));
        ok(said && typeof said.Words === 'string' && said.Words.length > 0, 'no words for the cycle');
    });

    await check('server', 'the sibling plugins are listed', async () => {
        const plugins = await api().getJSON(api().getUrl('LiteTv/Plugins'));
        ok(Array.isArray(plugins), 'Plugins did not answer with a list');
    });

    await check('server', 'the configuration document round trips', async () => {
        const config = await api().getPluginConfiguration(PLUGIN_ID);
        ok(config && typeof config === 'object', 'no configuration came back');
        // Channels live in their own files now; sending them back here would put them into the
        // one document that change took them out of.
        await api().updatePluginConfiguration(PLUGIN_ID, { ...config, Channels: [] });
    });
}

async function youtubeChecks(state) {
    await check('youtube', 'a playlist address is read, with its videos', async () => {
        const found = await api().getJSON(api().getUrl('LiteTv/YouTubePlaylist', { url: state.playlist }));
        ok(found && Array.isArray(found.Items), 'no Items in the answer');
        ok(found.Items.length > 0, 'the playlist came back empty - check the address, or the box has no internet');
        ok(found.Items[0].VideoId && found.Items[0].Url, 'a video with no id or address');
        state.playlistCount = found.Items.length;
        state.playlistTitle = found.Title;
    });

    await check('youtube', 'something that is not a playlist is refused, not guessed at', async () => {
        is(await statusOf(api().getJSON(api().getUrl('LiteTv/YouTubePlaylist', { url: 'not a playlist' }))), 400);
    });

    await check('youtube', 'a single video has a length', async () => {
        const answer = await api().getJSON(api().getUrl('LiteTv/Duration', { url: 'https://www.youtube.com/watch?v=' + state.aVideoId }));
        ok(answer && typeof answer.LengthSeconds === 'number', 'no LengthSeconds in the answer');
        ok(answer.LengthSeconds > 0, 'the video says it is zero seconds long');
        ok(answer.PlayableSeconds > 0, 'nothing of the video is playable');
    });
}

async function pageChecks(state) {
    await check('page', 'the app has drawn itself', async () => {
        ok(app() !== document.body, 'no LiteTV app on this page - open the configuration page, or inject the working copy');
        ok($$('[class*="rail"], [role="listbox"]').length > 0, 'nothing that looks like the channel rail');
    });

    await check('page', 'the Content screen shows the channel it is on', async () => {
        await openTab('Content');
        ok(searchBox(), 'no search box on the Content screen');
        state.sourcesBefore = sourceRows().length;
    });

    /*
        One box, and a series above its own episodes.

        Both halves are the owner's, days apart: "unified search bars everywhere ... films,
        series, episodes, collections and links", and then "the search should put the series first
        then episodes". The second is not a preference about sorting - with one request and a
        limit of twenty, a long-running show filled the answer with episodes and the series row,
        the one thing being looked for, was not in it at all.
    */
    await check('page', 'a title search puts the series above its episodes', async () => {
        typeInto(searchBox(), state.seriesName);
        await until('search results', () => resultRows().length > 0);
        const tags = resultRows().map(tagOf);
        const series = tags.indexOf('SERIES');
        const episode = tags.indexOf('EPISODE');
        ok(series !== -1, 'no series row for "' + state.seriesName + '" - it was ' + tags.join(', '));
        ok(episode !== -1, 'no episode rows at all for "' + state.seriesName + '" - nothing to order');
        ok(series < episode, 'the episodes came above the series: ' + tags.join(', '));
    });

    await check('page', 'an address is a result in the same list, and says how big it is', async () => {
        typeInto(searchBox(), state.playlist);
        await until('the link to be read', () => resultRows().some((r) => tagOf(r) === 'PLAYLIST'));
        const row = resultRows().find((r) => tagOf(r) === 'PLAYLIST');
        const detail = row.querySelector('.detail').textContent;
        ok(/\d+ videos?/.test(detail), 'the playlist row does not say how many videos: "' + detail + '"');
    });

    await check('page', 'clicking a link result puts it on the list', async () => {
        const row = resultRows().find((r) => tagOf(r) === 'PLAYLIST');
        clickLike(row);
        await until('the source list to grow', () => sourceRows().length === state.sourcesBefore + 1);
        const added = sourceRows()[sourceRows().length - 1];
        is(tagOf(added), 'YOUTUBE', 'the added row is not a YouTube one');
    });

    /*
        End to end, and it is the fault that started all this: the page composed a source the
        server could not read, and Save answered 400 with no words. Nothing else in this file
        exercises the page's OWN idea of what a link is - the server checks above post a body this
        file wrote, which proves the server and not the page.
    */
    await check('page', 'the channel saves with the link the page just made', async () => {
        const alerts = [];
        const alert = window.Dashboard.alert;
        window.Dashboard.alert = (m) => alerts.push(typeof m === 'string' ? m : m && m.message);
        try {
            const save = $$('button').find((b) => b.textContent.trim() === 'Save');
            ok(save && !save.disabled, 'the Save button is not offering to save');
            clickLike(save);
            await new Promise((r) => setTimeout(r, 1200));
            is(alerts, [], 'saving complained');
            const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), state.onScreenId);
            ok(back.Sources.some((s) => s.Type === 'YouTube' && s.Url === state.playlist), 'the link is not on the saved channel');
            state.mustRestore = true;
        } finally {
            window.Dashboard.alert = alert;
        }
    });

    await check('page', 'the bin takes it off again, and that saves too', async () => {
        const row = sourceRows()[sourceRows().length - 1];
        clickLike(row.querySelector('.bin'));
        await until('the source list to shrink', () => sourceRows().length === state.sourcesBefore);
        const save = $$('button').find((b) => b.textContent.trim() === 'Save');
        clickLike(save);
        await until('the link to be gone from the server', async () => {
            const back = byId(await api().getJSON(api().getUrl('LiteTv/Definitions')), state.onScreenId);
            return !back.Sources.some((s) => s.Url === state.playlist);
        });
        state.mustRestore = false;
    });

    /* ---- selecting a row: what it is for, and letting go of it ---- */

    const bar = () => $('.chosen');

    await check('page', 'clicking a row selects it and says what that does', async () => {
        clickLike(sourceRows()[0]);
        await until('the selection bar', () => bar());
        ok(bar().textContent.includes('is selected'), 'the bar does not say what is selected');
    });

    await check('page', 'clicking the same row again lets it go', async () => {
        clickLike(sourceRows()[0]);
        await until('the selection to clear', () => !bar());
    });

    /*
        The fault the owner reported as "can't unselect".

        The selection was an index that only a click on the row itself, or on the thin strip of
        list below the last row, could clear. Clicking ANYWHERE else on the page left a row
        marked selected with the keyboard nowhere near it: Delete then did nothing, and the only
        way back was to hunt the row down again. Turning your attention elsewhere is what lets it
        go now, and both ways of doing that are checked.
    */
    await check('page', 'pressing somewhere else on the page lets the selection go', async () => {
        clickLike(sourceRows()[0]);
        await until('the selection bar', () => bar());
        const elsewhere = $$('h2, h3, p, .none').find((n) => !n.closest('.scope')) || app();
        clickLike(elsewhere);
        await until('the selection to clear when the press landed elsewhere', () => !bar());
    });

    /*
        The same thing with the keyboard, and it can only be asked when this tab actually has the
        focus - `element.focus()` in a background tab moves `activeElement` and raises no event
        at all, which is a property of the window and not of the page. Skipped rather than failed
        when it cannot be asked, because a check that fails for being in the background teaches
        the next person to ignore it.
    */
    await check('page', 'the keyboard leaving the list lets it go too', async () => {
        if (!document.hasFocus()) { throw new Error('SKIPPED - this tab is in the background, so focus raises no events'); }
        clickLike(sourceRows()[0]);
        await until('the selection bar', () => bar());
        searchBox().focus();
        await until('the selection to clear when focus left', () => !bar());
    });

    await check('page', "the bar's own buttons do not count as leaving", async () => {
        const press = (word) => {
            const button = Array.from(bar().querySelectorAll('button')).find((b) => b.textContent.trim() === word);
            ok(button, 'no "' + word + '" button in the bar');
            clickLike(button);
            return new Promise((r) => setTimeout(r, 150));
        };

        clickLike(sourceRows()[1]);
        await until('the selection bar', () => bar());
        try {
            await press('Move up');
            ok(bar(), 'pressing a button in the bar threw the selection away');
            is(sourceRows()[0].querySelector('.name').textContent, state.namesBefore[1], 'Move up did not move the row');
        } finally {
            if (bar()) { await press('Move down'); }
            searchBox().focus();
        }
    });

    await check('page', 'Escape lets it go too', async () => {
        clickLike(sourceRows()[0]);
        await until('the selection bar', () => bar());
        sourceRows()[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await until('the selection to clear on Escape', () => !bar());
    });

    await check('page', 'the order the list was found in is the order it is left in', async () => {
        is(sourceRows().length, state.sourcesBefore, 'the list is a different length than it was found');
        is(sourceRows().map((r) => r.querySelector('.name').textContent), state.namesBefore, 'the rows have been reordered');
    });

    /* ---- the television preview on the Look screen ---- */

    /*
        The preview's whole job is the CROP, so its shape is asserted against the app's own
        numbers: `ChannelRow` is 78dp tall with a banner beside it at 1000:185 taking 48% of the
        width, which is 11.26 to one. Getting that right is what made the words overlap - at this
        column's width the row is about 30px tall - so the words are checked too, and the check
        is simply that they still fit inside it.
    */
    await check('page', 'the channel-list preview is the app\'s shape, with its words inside it', async () => {
        await openTab('Look');
        const row = await until('the television preview', () => $('.tv-row'));
        const art = row.querySelector('.tv-row-art');
        const text = row.querySelector('.tv-row-text');
        const r = row.getBoundingClientRect();
        const a = art.getBoundingClientRect();

        is(Number((r.width / r.height).toFixed(2)), 11.26, 'the row is not the app\'s 78dp row');
        is(Number((a.width / a.height).toFixed(2)), 5.41, 'the picture is not at a banner\'s 1000:185');
        is(Number((a.width / r.width).toFixed(2)), 0.48, 'the picture does not take its 48% of the row');
        ok(text.scrollHeight <= text.clientHeight + 1,
            'the words do not fit the row: ' + text.scrollHeight + 'px of text in ' + text.clientHeight + 'px');

        // Every line separate, in the order the app draws them - overlapping is what "squished"
        // was, and two lines at the same top is exactly what it looks like from here.
        const tops = Array.from(text.children).map((k) => k.getBoundingClientRect().top);
        for (let i = 1; i < tops.length; i++) {
            ok(tops[i] > tops[i - 1], 'line ' + i + ' of the preview sits on top of the one above it');
        }
        await openTab('Content');
    });

    /* ---- every screen draws, and none of them widen the page ---- */

    await check('page', 'every tab shows exactly one screen, and none scrolls sideways', async () => {
        const wide = [];
        for (const tab of [
            ['Week', 'Woche'], ['Content', 'Inhalt'], ['Breaks', 'Pausen'],
            ['Look', 'Aussehen'], ['Settings', 'Einstellungen'],
        ]) {
            await openTab(tab[0]);
            if (!document.querySelector('.tab.on')?.textContent.match(new RegExp(tab[0] + '|' + tab[1]))) {
                throw new Error('the active tab has no expected label');
            }
            await new Promise((r) => setTimeout(r, 200));
            ok(app().textContent.trim().length > 0, 'the ' + tab[0] + ' screen drew nothing');
            if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) { wide.push(tab[0]); }
        }
        is(wide, [], 'these screens make the page scroll sideways');
        await openTab('Content');
    });
}

/** An episode from this library, for the checks that need a real id. */
async function someEpisode() {
    const answer = await api().getItems(api().getCurrentUserId(), {
        includeItemTypes: 'Episode', recursive: true, limit: 1,
    });
    const item = (answer.Items || [])[0];
    if (!item) { throw new Error('this library has no episodes, so nothing can be checked against one'); }
    return item;
}

/**
 * A word this library answers with BOTH a series and some episodes.
 *
 * Not the series' own name. Jellyfin matches an episode on the episode's title, not its series',
 * so searching "The Boys" finds the series and none of its episodes - and a check that wanted
 * both then failed for a reason that had nothing to do with the ordering it was testing. The
 * term is found in the library instead: words from series and episode names are tried until one
 * comes back with both kinds behind it.
 */
async function termWithBoth() {
    const seen = new Set();
    const words = [];
    for (const type of ['Series', 'Episode']) {
        const answer = await api().getItems(api().getCurrentUserId(), {
            includeItemTypes: type, recursive: true, limit: 60,
        });
        for (const item of answer.Items || []) {
            for (const word of String(item.Name).split(/[^\p{L}\p{N}]+/u)) {
                if (word.length >= 4 && !seen.has(word.toLowerCase())) {
                    seen.add(word.toLowerCase());
                    words.push(word);
                }
            }
        }
    }

    for (const word of words) {
        const [series, episodes] = await Promise.all([
            api().getItems(api().getCurrentUserId(), { searchTerm: word, includeItemTypes: 'Series', recursive: true, limit: 5 }),
            api().getItems(api().getCurrentUserId(), { searchTerm: word, includeItemTypes: 'Episode', recursive: true, limit: 5 }),
        ]);
        if ((series.Items || []).length > 0 && (episodes.Items || []).length > 0) { return word; }
    }

    throw new Error('no word in this library matches both a series and an episode - the ordering cannot be judged here');
}

/* ------------------------------------------------------------------ running it */

export async function run(opts = {}) {
    checks.length = 0;
    only = opts.only || null;

    if (!window.ApiClient) {
        throw new Error('No ApiClient - this has to run in a Jellyfin dashboard tab.');
    }

    const state = {
        playlist: opts.playlist || DEFAULT_PLAYLIST,
        aVideoId: opts.video || 'dQw4w9WgXcQ',
        tempId: newId(),
        namesBefore: [],
    };

    try {
        await serverChecks(state);
        await youtubeChecks(state);

        if (opts.page !== false && document.getElementById('litetv-app')) {
            state.seriesName = await termWithBoth();

            /*
                The channel with the most sources, and not whichever one the page happened to
                open on. Selecting a row, moving it and putting it back needs at least two rows
                to be a test of anything, and the page opens on the first channel in the rail -
                which here has one.
            */
            const richest = state.channels
                .slice()
                .sort((a, b) => (b.Sources || []).length - (a.Sources || []).length)[0];
            state.onScreenId = richest.Id;
            await openChannel(richest.Name);
            await openTab('Content');

            await until('the channel to be drawn', () => sourceRows().length === (richest.Sources || []).length);
            state.namesBefore = sourceRows().map((r) => r.querySelector('.name').textContent);
            await pageChecks(state);
        }
    } finally {
        // The temporary channel goes whatever happened, and its week with it.
        await api().fetch({ url: api().getUrl('LiteTv/Definitions/' + state.tempId), type: 'DELETE' }).catch(() => { });
    }

    const skipped = checks.filter((c) => c.skipped).length;
    const passed = checks.filter((c) => c.ok && !c.skipped).length;
    const failed = checks.length - passed - skipped;
    console.table(checks.map((c) => ({
        group: c.group,
        check: c.name,
        ok: c.skipped ? 'skip' : (c.ok ? 'pass' : 'FAIL'),
        ms: c.ms,
        why: c.why || '',
    })));
    console.info('[litetv suite] ' + passed + ' passed, ' + failed + ' failed, ' + skipped + ' skipped');
    return { passed, failed, skipped, checks };
}

window.__ltvSuite = run;
