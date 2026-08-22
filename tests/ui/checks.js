/*
 * The configuration page's UI suite.
 *
 * It exists because two releases in a row shipped a page that was visibly broken while the
 * build was clean and every C# test passed: the stylesheet went in the <head>, which Jellyfin
 * throws away, and the layout then had no chance. A C# test cannot see a page. This can.
 *
 * Everything here is an assertion about the *page*, run against the real configPage.html with a
 * stubbed ApiClient. Call window.__ltvChecks() and read the result.
 */
(function () {
    'use strict';

    var results = [];

    function check(name, fn) {
        try {
            var detail = fn();
            results.push({ name: name, pass: true, detail: detail === undefined ? '' : String(detail) });
        } catch (err) {
            results.push({ name: name, pass: false, detail: err && err.message ? err.message : String(err) });
        }
    }

    function assert(condition, message) {
        if (!condition) { throw new Error(message); }
    }

    function q(sel) { return document.querySelector(sel); }
    function all(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); }

    function visiblePane() {
        return all('.litetvPane').filter(function (p) { return p.classList.contains('selected'); });
    }

    function clickTab(name) {
        var tab = all('.litetvTab').filter(function (t) { return t.dataset.pane === name; })[0];
        assert(tab, 'no tab for ' + name);
        tab.click();
    }

    /** Some of the page arrives over the wire, so a check for it has to wait rather than look once. */
    function until(test, ms) {
        var deadline = Date.now() + (ms || 3000);
        return new Promise(function (resolve) {
            (function poll() {
                if (test() || Date.now() > deadline) { return resolve(test()); }
                setTimeout(poll, 50);
            })();
        });
    }

    function checkAsync(name, fn) {
        return Promise.resolve().then(fn).then(function (detail) {
            results.push({ name: name, pass: true, detail: detail === undefined ? '' : String(detail) });
        }).catch(function (err) {
            results.push({ name: name, pass: false, detail: err && err.message ? err.message : String(err) });
        });
    }

    /** Anything wider than the box holding it puts a sideways scrollbar on a screen. */
    function overflowingElements() {
        return all('.litetvPane.selected *').filter(function (el) {
            return el.scrollWidth > el.clientWidth + 2 && getComputedStyle(el).overflowX === 'visible';
        }).map(function (el) {
            return (el.className || el.tagName) + ' ' + el.scrollWidth + '>' + el.clientWidth;
        });
    }

    window.__ltvChecks = async function () {
        results = [];

        // --- the fault that shipped in v1.0.61.0 -------------------------------------------
        check('the stylesheet reaches the browser', function () {
            var found = false;
            for (var i = 0; i < document.styleSheets.length; i++) {
                try {
                    var rules = document.styleSheets[i].cssRules;
                    for (var r = 0; r < rules.length; r++) {
                        if (rules[r].cssText && rules[r].cssText.indexOf('ltvRail') >= 0) { found = true; }
                    }
                } catch (e) { /* a foreign sheet; not ours */ }
            }
            assert(found, 'no .ltvRail rule in any stylesheet - the page is unstyled');
            return 'ltvRail rules present';
        });

        check('the stylesheet is inside the body, not the head', function () {
            var inHead = document.head.querySelector('style');
            var inPage = q('#LiteTvConfigPage > style');
            assert(inPage, 'no <style> inside the page div - Jellyfin discards the head');
            assert(!inHead || inHead.textContent.indexOf('ltvRail') < 0,
                'the page stylesheet is in the head, where it will be thrown away');
            return 'in the page div';
        });

        check('the rail is laid out, not stacked', function () {
            var rail = q('.ltvRail');
            assert(rail, 'no rail');
            assert(getComputedStyle(q('.ltv')).display === 'flex', '.ltv is not flex - css did not apply');
            assert(getComputedStyle(rail).display === 'flex', 'the rail is not flex');
            return 'flex';
        });

        // --- the rule the whole design exists to keep -------------------------------------
        check('the document itself does not scroll', function () {
            var over = document.documentElement.scrollHeight - window.innerHeight;
            assert(over <= 4, 'the page scrolls by ' + over + 'px - only the rail may scroll');
            return 'fits';
        });

        check('only the rail scrolls vertically', function () {
            var scrollers = all('.ltv *').filter(function (el) {
                var cs = getComputedStyle(el);
                return (cs.overflowY === 'auto' || cs.overflowY === 'scroll')
                    && el.scrollHeight > el.clientHeight + 2;
            });
            var offenders = scrollers.filter(function (el) {
                return !el.classList.contains('ltvRailList')
                    && !el.classList.contains('ltvScroll')
                    && !el.closest('#WeekTimeline')
                    && !el.classList.contains('paperList');
            });
            assert(offenders.length === 0, 'unexpected scrollers: ' + offenders.map(function (e) { return e.className; }).join(', '));
            return scrollers.length + ' permitted scrollers';
        });

        // --- the rail ---------------------------------------------------------------------
        check('the rail lists every channel', function () {
            var rows = all('#ChannelRail .ltvRailRow');
            assert(rows.length === 16, 'expected 16 rail rows, found ' + rows.length);
            assert(q('#ChannelCount').textContent.indexOf('16') >= 0, 'the count does not say 16');
            return rows.length + ' rows';
        });

        check('the filter narrows the rail and says so', function () {
            var input = q('#ChannelFilter');
            input.value = 'kanal';
            input.dispatchEvent(new Event('input'));
            var rows = all('#ChannelRail .ltvRailRow').length;
            var count = q('#ChannelCount').textContent;
            input.value = '';
            input.dispatchEvent(new Event('input'));
            assert(rows > 0 && rows < 16, 'filter matched ' + rows + ' rows');
            assert(count.indexOf('of') >= 0, 'the count does not say "x of y" while filtering');
            assert(all('#ChannelRail .ltvRailRow').length === 16, 'clearing the filter did not restore the list');
            return rows + ' matched "kanal"';
        });

        check('a channel with no week wears the tag', function () {
            assert(all('#ChannelRail .ltvTag').length > 0, 'no "no week" tag anywhere');
            return all('#ChannelRail .ltvTag').length + ' tagged';
        });

        // --- tabs and panes ---------------------------------------------------------------
        ['week', 'content', 'breaks', 'look', 'settings'].forEach(function (pane) {
            check('the ' + pane + ' tab shows exactly one pane', function () {
                clickTab(pane);
                var shown = visiblePane();
                assert(shown.length === 1, shown.length + ' panes visible');
                assert(shown[0].dataset.pane === pane, 'showed ' + shown[0].dataset.pane);
                var lit = all('.litetvTab.selected');
                assert(lit.length === 1 && lit[0].dataset.pane === pane, 'the wrong tab is lit');
                return 'ok';
            });

            check('the ' + pane + ' screen does not overflow sideways', function () {
                clickTab(pane);
                var bad = overflowingElements();
                assert(bad.length === 0, bad.join(' | '));
                return 'no overflow';
            });
        });

        // --- the week ---------------------------------------------------------------------
        check('the week draws seven days of bars', function () {
            clickTab('week');
            var columns = all('#WeekTimeline [data-day-index]');
            assert(columns.length === 7, 'expected 7 day columns, found ' + columns.length);
            var bars = all('#WeekTimeline [data-day-index] > div');
            assert(bars.length > 20, 'only ' + bars.length + ' bars drawn');
            return columns.length + ' days, ' + bars.length + ' bars';
        });

        check('the week grid opens on the evening, not on midnight', function () {
            clickTab('week');
            var frame = q('#WeekTimeline').firstElementChild;
            assert(frame && frame.scrollTop > 0, 'the grid is scrolled to the top of the day');
            return 'scrollTop ' + Math.round(frame.scrollTop);
        });

        check('the day view shows one day', function () {
            clickTab('week');
            var dayButton = all('#WeekViewToggle button').filter(function (b) { return b.dataset.view === 'day'; })[0];
            dayButton.click();
            var columns = all('#WeekTimeline [data-day-index]').length;
            var picker = q('#WeekDayPicker');
            var pickerShown = getComputedStyle(picker).display !== 'none';
            all('#WeekViewToggle button')[0].click();
            assert(columns === 1, 'day view drew ' + columns + ' columns');
            assert(pickerShown, 'the day picker is hidden in day view');
            return 'one column, picker shown';
        });

        check('zoom changes the height of the grid', function () {
            clickTab('week');
            var track = q('#WeekTimeline [data-day-index]');
            var before = track.getBoundingClientRect().height;
            var zoom = q('#WeekZoom');
            zoom.value = '120';
            zoom.dispatchEvent(new Event('input'));
            var after = q('#WeekTimeline [data-day-index]').getBoundingClientRect().height;
            zoom.value = '46';
            zoom.dispatchEvent(new Event('input'));
            assert(after > before, 'zooming in did not make the day taller (' + before + ' → ' + after + ')');
            return Math.round(before) + ' → ' + Math.round(after) + 'px';
        });

        // --- settings, help, destinations -------------------------------------------------
        check('a (?) opens its explanation and closes it again', function () {
            clickTab('settings');
            var why = q('.litetvPane.selected .ltvWhy');
            assert(why, 'no help button on the settings screen');
            var help = document.getElementById(why.dataset.help);
            assert(help && !help.classList.contains('open'), 'help starts open');
            why.click();
            assert(help.classList.contains('open'), 'clicking (?) did not open it');
            why.click();
            assert(!help.classList.contains('open'), 'clicking (?) again did not close it');
            return 'toggles';
        });

        await checkAsync('settings shows a real evening from the server', async function () {
            clickTab('settings');
            await until(function () { return q('#SettingsPreview').children.length > 3; });
            var rows = q('#SettingsPreview').children.length;
            assert(rows > 3, 'the preview drew ' + rows + ' rows');
            return rows + ' rows';
        });

        check('the destinations hide the channel tabs and light only themselves', function () {
            var dest = all('.ltvRailDests .ltvRailRow')[0];
            dest.click();
            var tabsHidden = getComputedStyle(q('#ChannelTabs')).visibility === 'hidden';
            var litRail = all('.ltvRailRow.selected');
            var shown = visiblePane();
            assert(shown.length === 1 && shown[0].dataset.pane === 'server', 'server pane not shown');
            assert(tabsHidden, 'the channel tabs are still visible on a destination');
            assert(litRail.length === 1, litRail.length + ' rail rows look selected');
            return 'ok';
        });

        await checkAsync('the plugin strip lists the sibling plugins', async function () {
            await until(function () { return q('#SiblingPlugins').children.length >= 4; });
            var rows = q('#SiblingPlugins').children.length;
            assert(rows >= 4, 'the strip drew ' + rows + ' rows');
            return rows + ' plugins';
        });

        check('going back to a channel restores its editor', function () {
            all('#ChannelRail .ltvRailRow')[0].click();
            var shown = visiblePane();
            assert(shown.length === 1, shown.length + ' panes visible');
            assert(getComputedStyle(q('#ChannelTabs')).visibility !== 'hidden', 'the tabs stayed hidden');
            return 'back on ' + shown[0].dataset.pane;
        });

        check('the header names the channel that is selected', function () {
            all('#ChannelRail .ltvRailRow')[1].click();
            var name = q('#HeadName').textContent.trim();
            assert(name.length > 0 && name !== 'LiteTV', 'the header says "' + name + '"');
            return name;
        });

        check('saving posts the configuration', function () {
            window.__ltvSaved = false;
            q('#LiteTvConfigForm').dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
            assert(window.__ltvSaved, 'submitting the form did not call updatePluginConfiguration');
            return 'posted';
        });

        var failed = results.filter(function (r) { return !r.pass; });
        return {
            total: results.length,
            failed: failed.length,
            failures: failed,
            passes: results.filter(function (r) { return r.pass; }).map(function (r) { return r.name + ' — ' + r.detail; })
        };
    };
})();
