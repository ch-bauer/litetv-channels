(function () {
    'use strict';

    // LiteTV Channels: lightweight virtual TV channels for the Jellyfin web client.
    // Injected by the LiteTV Channels plugin. Renders a channel guide (home row +
    // header button), tunes into channels via normal direct playback at the live
    // position, and drives the end-of-episode continue/schedule flow.

    var HOME_ROW_ID = 'liteTvHomeRow';
    var GUIDE_ID = 'liteTvGuide';
    var STYLE_ID = 'liteTvStyle';
    var TUNE_OVERLAY_ID = 'liteTvTuneOverlay';
    var NEXT_OVERLAY_ID = 'liteTvNextOverlay';
    var PAUSE_PANEL_ID = 'liteTvPausePanel';
    var TUNED_BODY_CLASS = 'liteTvTuned';
    var NEXT_OVERLAY_WINDOW_SECONDS = 45;
    // Fallback margin (seconds before the real end) for advancing to the follow-up
    // when an item has no outro segment to skip to. With a Media Segments outro the
    // schedule advances precisely at the credits instead.
    var NEXT_ADVANCE_SECONDS = 15;

    // Tuned state for this browser tab. mode: 'schedule' | 'binge' | 'offschedule'
    var tuned = null;
    var watchTimer = null;
    var chainInProgress = false;
    var homeRowObserver = null;
    var lastHomeRowGuide = null;

    function apiGet(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'GET', dataType: 'json' });
    }

    function apiPost(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'POST' });
    }

    function apiDelete(path) {
        return window.ApiClient.fetch({ url: window.ApiClient.getUrl(path), type: 'DELETE' });
    }

    function getOwnSession() {
        var apiClient = window.ApiClient;
        return apiClient.getSessions({ deviceId: apiClient.deviceId() }).then(function (sessions) {
            return (sessions || [])[0] || null;
        });
    }

    function formatTime(isoUtc) {
        try {
            return new Date(isoUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        } catch (e) {
            return '';
        }
    }

    // Keeps overlay button interactions from reaching the video OSD underneath
    // (a plain click on the OSD surface toggles play/pause).
    function swallow(e) {
        e.preventDefault();
        e.stopPropagation();
    }

    function makeButton(label, className, onClick) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = className;
        btn.textContent = label;
        ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'touchstart', 'touchend'].forEach(function (evt) {
            btn.addEventListener(evt, function (e) { e.stopPropagation(); });
        });
        btn.addEventListener('click', function (e) {
            swallow(e);
            onClick(e);
        });
        return btn;
    }

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            /* ---- suppress Jellyfin's own Next Up while a channel is tuned ---- */
            'body.' + TUNED_BODY_CLASS + ' .upNextContainer,' +
            'body.' + TUNED_BODY_CLASS + ' .upNextDialog { display: none !important; }' +

            /* ---------------------------------------------------- home row ---- */
            '#' + HOME_ROW_ID + ' { padding: 0 3.3%; margin-bottom: 1.2em; }' +
            '#' + HOME_ROW_ID + ' .liteTvCards { display: flex; gap: 1em; overflow-x: auto; padding: 0.3em 0.15em 0.6em; scrollbar-width: thin; }' +
            '.liteTvCard {' +
            '  position: relative; min-width: 19em; max-width: 19em; min-height: 9.5em;' +
            '  border-radius: 0.75em; overflow: hidden; cursor: pointer; color: #fff;' +
            '  background-color: #1c2733; background-size: cover; background-position: center;' +
            '  box-shadow: 0 0.15em 0.7em rgba(0, 0, 0, 0.35);' +
            '  transition: transform 0.22s ease, box-shadow 0.22s ease;' +
            '}' +
            '.liteTvCard:hover { transform: translateY(-0.18em) scale(1.015); box-shadow: 0 0.4em 1.3em rgba(0, 0, 0, 0.5); }' +
            '.liteTvCardShade { position: absolute; inset: 0; background: linear-gradient(180deg, rgba(8,10,14,0.05) 0%, rgba(8,10,14,0.45) 55%, rgba(8,10,14,0.88) 100%); }' +
            '.liteTvChannelChip {' +
            '  position: absolute; top: 0.75em; left: 0.8em;' +
            '  background: rgba(10, 12, 16, 0.55); backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px);' +
            '  border: 1px solid rgba(255,255,255,0.14); border-radius: 999px;' +
            '  padding: 0.28em 0.85em; font-size: 0.82em; font-weight: 600; letter-spacing: 0.03em;' +
            '}' +
            '.liteTvCardBody { position: absolute; inset: auto 0 0 0; padding: 0.9em 1em 0.85em; }' +
            '.liteTvNow { font-size: 1em; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; text-shadow: 0 1px 4px rgba(0,0,0,0.7); }' +
            '.liteTvNext { font-size: 0.82em; opacity: 0.8; margin-top: 0.35em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.liteTvProgress { height: 0.22em; border-radius: 999px; background: rgba(255,255,255,0.22); margin-top: 0.55em; overflow: hidden; }' +
            '.liteTvProgress > div { height: 100%; border-radius: 999px; background: linear-gradient(90deg, #00a4dc, #4dd0ff); }' +

            /* ------------------------------------------------ header button ---- */
            '#liteTvHeaderBtn { margin: 0 0.2em; font-size: 1.15em; }' +

            /* ------------------------------------------------------- guide ---- */
            '#' + GUIDE_ID + ' {' +
            '  position: fixed; inset: 0; z-index: 1200;' +
            '  background: rgba(6, 8, 12, 0.6); backdrop-filter: blur(6px); -webkit-backdrop-filter: blur(6px);' +
            '  display: flex; align-items: center; justify-content: center;' +
            '  opacity: 0; transition: opacity 0.25s ease;' +
            '}' +
            '#' + GUIDE_ID + '.liteTvVisible { opacity: 1; }' +
            '#' + GUIDE_ID + ' .liteTvPanel {' +
            '  background: rgba(24, 27, 33, 0.94); color: #fff; border: 1px solid rgba(255,255,255,0.09);' +
            '  border-radius: 1em; width: min(36em, 92vw); max-height: 82vh; overflow-y: auto;' +
            '  padding: 1.4em 1.6em; box-shadow: 0 1em 3em rgba(0,0,0,0.6);' +
            '  font-size: clamp(12px, 1.05vw, 16px);' +
            '}' +
            '#' + GUIDE_ID + ' h2 { margin: 0 0 0.9em; font-size: 1.35em; font-weight: 700; letter-spacing: 0.01em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideChannel { border-top: 1px solid rgba(255,255,255,0.09); padding: 0.95em 0; }' +
            '#' + GUIDE_ID + ' .liteTvGuideChannel:first-of-type { border-top: 0; }' +
            '#' + GUIDE_ID + ' .liteTvGuideHead { display: flex; align-items: center; justify-content: space-between; gap: 0.8em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideName { font-weight: 700; font-size: 1.12em; }' +
            '#' + GUIDE_ID + ' .liteTvGuideActions { display: flex; gap: 0.5em; flex-shrink: 0; }' +
            '#' + GUIDE_ID + ' .liteTvEpg { margin: 0.5em 0 0; font-size: 0.9em; opacity: 0.85; line-height: 1.5; }' +
            '#' + GUIDE_ID + ' .liteTvEpg .liteTvEpgTime { color: #4dd0ff; font-variant-numeric: tabular-nums; margin-right: 0.4em; }' +
            '#' + GUIDE_ID + ' .liteTvDevices { margin-top: 0.6em; display: none; }' +
            '#' + GUIDE_ID + ' .liteTvDevices .liteTvPill { display: block; width: 100%; text-align: left; margin: 0.35em 0; }' +

            /* ---------------------------------------------- shared buttons ---- */
            '.liteTvPill {' +
            '  appearance: none; border-radius: 999px; cursor: pointer; font-weight: 600; font-size: 0.95em;' +
            '  padding: 0.5em 1.15em; color: #fff; background: rgba(255,255,255,0.09);' +
            '  border: 1px solid rgba(255,255,255,0.28);' +
            '  transition: background 0.18s ease, border-color 0.18s ease, transform 0.12s ease;' +
            '}' +
            '.liteTvPill:hover { background: rgba(255,255,255,0.2); }' +
            '.liteTvPill:active { transform: scale(0.97); }' +
            '.liteTvPillPrimary { background: #00a4dc; border-color: transparent; }' +
            '.liteTvPillPrimary:hover { background: #14b4ec; }' +
            '.liteTvPillActive { background: #00a4dc; border-color: transparent; box-shadow: 0 0 0.7em rgba(0,164,220,0.55); }' +

            /* -------------------------------------------- playback overlays ---- */
            '#' + TUNE_OVERLAY_ID + ', #' + NEXT_OVERLAY_ID + ' {' +
            '  position: absolute; z-index: 1000; pointer-events: none;' +
            '  opacity: 0; transform: translateY(0.5em); transition: opacity 0.45s ease, transform 0.45s ease;' +
            '  font-size: clamp(11px, 1.1vw, 17px); color: #fff; font-family: inherit;' +
            '}' +
            '#' + TUNE_OVERLAY_ID + '.liteTvVisible, #' + NEXT_OVERLAY_ID + '.liteTvVisible { opacity: 1; transform: translateY(0); }' +
            '#' + TUNE_OVERLAY_ID + ' { top: 7%; right: 4%; display: flex; flex-direction: column; align-items: flex-end; gap: 0.6em; }' +
            '#' + TUNE_OVERLAY_ID + ' .liteTvBug {' +
            '  background: rgba(14, 16, 20, 0.6); backdrop-filter: blur(10px); -webkit-backdrop-filter: blur(10px);' +
            '  border: 1px solid rgba(255,255,255,0.16); border-radius: 999px;' +
            '  padding: 0.5em 1.15em; font-weight: 700; letter-spacing: 0.02em;' +
            '  box-shadow: 0 0.3em 1.2em rgba(0,0,0,0.45);' +
            '}' +
            '#' + TUNE_OVERLAY_ID + ' .liteTvPill { pointer-events: auto; background: rgba(14, 16, 20, 0.6); backdrop-filter: blur(10px); -webkit-backdrop-filter: blur(10px); }' +
            '#' + TUNE_OVERLAY_ID + ' .liteTvPill:hover { background: rgba(255,255,255,0.22); }' +
            '#' + NEXT_OVERLAY_ID + ' { bottom: 16%; right: 4%; }' +
            '.liteTvNextCard {' +
            '  background: rgba(16, 18, 23, 0.72); backdrop-filter: blur(14px); -webkit-backdrop-filter: blur(14px);' +
            '  border: 1px solid rgba(255,255,255,0.12); border-radius: 1em;' +
            '  padding: 1.05em 1.25em 1.15em; min-width: 19em; max-width: 26em; text-align: left;' +
            '  box-shadow: 0 0.6em 2.2em rgba(0,0,0,0.55);' +
            '}' +
            '.liteTvEyebrow { font-size: 0.7em; letter-spacing: 0.22em; text-transform: uppercase; opacity: 0.65; margin-bottom: 0.45em; }' +
            '.liteTvNextName { font-size: 1.18em; font-weight: 700; line-height: 1.3; margin-bottom: 0.2em; }' +
            '.liteTvCountdownText { font-size: 0.85em; opacity: 0.7; margin-bottom: 0.9em; font-variant-numeric: tabular-nums; }' +
            '.liteTvNextButtons { display: flex; gap: 0.55em; flex-wrap: wrap; }' +
            '.liteTvNextButtons .liteTvPill { pointer-events: auto; }' +
            '.liteTvCountdownBar { height: 0.2em; border-radius: 999px; background: rgba(255,255,255,0.16); margin-top: 1em; overflow: hidden; }' +
            '.liteTvCountdownBar > div { height: 100%; background: linear-gradient(90deg, #00a4dc, #4dd0ff); transition: width 1s linear; }' +

            /* -------------------------------------------------- pause panel ---- */
            '#' + PAUSE_PANEL_ID + ' { pointer-events: auto; font-size: clamp(11px, 1.1vw, 17px); }' +
            '#' + PAUSE_PANEL_ID + '.liteTvPauseFloating { position: absolute; left: 4%; bottom: 14%; z-index: 1000; margin: 0; }' +
            /* Inside Jellyfin Enhanced's pause screen everything is absolutely positioned; */
            /* top-right is free (logo/details/plot live on the left, the disc sits mid-right). */
            '#pause-screen-content #' + PAUSE_PANEL_ID + ' { position: absolute; right: 5vw; top: 7vh; margin: 0; z-index: 5; }' +
            '#' + PAUSE_PANEL_ID + ' .liteTvPauseMeta { font-size: 0.9em; opacity: 0.85; line-height: 1.55; margin-bottom: 0.95em; }' +
            '#' + PAUSE_PANEL_ID + ' .liteTvPauseMeta .liteTvEpgTime { color: #4dd0ff; margin-right: 0.4em; font-variant-numeric: tabular-nums; }';
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- playback

    function playItem(itemId, positionTicks, channelId) {
        channelId = channelId || (tuned && tuned.channelId);
        return getOwnSession().then(function (session) {
            if (!session) {
                throw new Error('own session not found');
            }
            // Snapshot the item's watch state on the server before playback begins, so
            // the played flag, play count and resume position can be restored afterwards
            // (they are bumped around playback start, too late to catch at PlaybackStart).
            var prepare = channelId
                ? apiPost('LiteTv/Tuned?sessionId=' + encodeURIComponent(session.Id) + '&channelId=' + channelId + '&itemId=' + encodeURIComponent(itemId)).catch(function () { })
                : Promise.resolve();
            return prepare.then(function () {
                var url = window.ApiClient.getUrl('Sessions/' + session.Id + '/Playing', {
                    playCommand: 'PlayNow',
                    itemIds: itemId,
                    startPositionTicks: Math.max(0, Math.round(positionTicks || 0))
                });
                return window.ApiClient.fetch({ url: url, type: 'POST' }).then(function () {
                    return session.Id;
                });
            });
        });
    }

    function tuneIn(channelId) {
        closeGuide();
        return apiGet('LiteTv/Channels/' + channelId + '/Now?upcoming=1').then(function (now) {
            // Pass the channel id so playItem marks the session tuned and snapshots the
            // first item's watch state before playback starts.
            return playItem(now.Current.ItemId, now.OffsetTicks, channelId).then(function (sessionId) {
                tuned = {
                    channelId: channelId,
                    channelName: now.ChannelName,
                    sessionId: sessionId,
                    mode: 'schedule',
                    currentItemId: now.Current.ItemId,
                    currentSeriesId: now.Current.SeriesId || null
                };
                chainInProgress = true; // survives the osd transition
                document.body.classList.add(TUNED_BODY_CLASS);
                showTuneOverlay(now);
            });
        }).catch(function (err) {
            console.warn('liteTv: tune-in failed', err);
        });
    }

    function untune() {
        if (!tuned) {
            return;
        }
        var sessionId = tuned.sessionId;
        tuned = null;
        chainInProgress = false;
        stopWatcher();
        document.body.classList.remove(TUNED_BODY_CLASS);
        removeOverlay(TUNE_OVERLAY_ID);
        removeOverlay(NEXT_OVERLAY_ID);
        removePausePanel();
        if (sessionId) {
            apiDelete('LiteTv/Tuned?sessionId=' + encodeURIComponent(sessionId)).catch(function () { });
        }
    }

    // Closing the tab or navigating away skips untune(), which would leave the item that
    // was on air holding the server's snapshot until the tuned session expires - i.e.
    // marked watched for hours. A keepalive request still goes out during unload.
    window.addEventListener('pagehide', function () {
        if (!tuned || !tuned.sessionId) {
            return;
        }
        try {
            var headers = {};
            if (typeof window.ApiClient.setRequestHeaders === 'function') {
                window.ApiClient.setRequestHeaders(headers);
            }
            window.fetch(window.ApiClient.getUrl('LiteTv/Tuned?sessionId=' + encodeURIComponent(tuned.sessionId)), {
                method: 'DELETE',
                headers: headers,
                keepalive: true
            });
        } catch (e) {
            /* nothing left to do while the page is going away */
        }
    });

    // ---------------------------------------------------------------- overlays

    function getOsdContainer() {
        return document.querySelector('#videoOsdPage:not(.hide)') || document.querySelector('#videoOsdPage') || document.body;
    }

    function removeOverlay(id) {
        var el = document.getElementById(id);
        if (el && el.parentNode) {
            el.parentNode.removeChild(el);
        }
    }

    function showTuneOverlay(now) {
        ensureStyle();
        removeOverlay(TUNE_OVERLAY_ID);

        var overlay = document.createElement('div');
        overlay.id = TUNE_OVERLAY_ID;

        var bug = document.createElement('div');
        bug.className = 'liteTvBug';
        bug.textContent = '📺 ' + now.ChannelName;
        overlay.appendChild(bug);

        overlay.appendChild(makeButton('↺ Von Anfang an', 'liteTvPill', function () {
            removeOverlay(TUNE_OVERLAY_ID);
            if (!tuned) {
                return;
            }
            tuned.mode = 'offschedule';
            chainInProgress = true;
            playItem(tuned.currentItemId, 0).catch(function (err) {
                console.warn('liteTv: restart failed', err);
            });
        }));

        getOsdContainer().appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');

        setTimeout(function () {
            overlay.classList.remove('liteTvVisible');
            setTimeout(function () { removeOverlay(TUNE_OVERLAY_ID); }, 600);
        }, 8000);
    }

    // ------------------------------------------------------------ pause panel

    // Shown while playback is paused: inside Jellyfin Enhanced's custom pause
    // screen when that is active (#pause-screen-content), otherwise floating over
    // the OSD. Tells the viewer they are watching a LiteTV channel and offers the
    // mode options without waiting for the end-of-episode overlay.
    function removePausePanel() {
        removeOverlay(PAUSE_PANEL_ID);
    }

    function ensurePausePanel() {
        if (!tuned) {
            return;
        }

        // With Jellyfin Enhanced's custom pause screen installed, the panel is
        // shown only together with that screen (and inside it), so the two stay
        // in sync. Without it, the panel floats over the OSD while paused.
        var jeInstalled = !!document.getElementById('pause-screen-style');
        var jeActive = document.documentElement.classList.contains('pause-screen-active');
        var jeContent = jeActive ? document.getElementById('pause-screen-content') : null;
        if (jeInstalled && !jeContent) {
            removePausePanel();
            return;
        }
        var host = jeContent || getOsdContainer();

        var panel = document.getElementById(PAUSE_PANEL_ID);
        if (panel && panel.parentNode !== host) {
            panel.parentNode.removeChild(panel);
            panel = null;
        }
        if (panel) {
            return;
        }

        ensureStyle();
        panel = document.createElement('div');
        panel.id = PAUSE_PANEL_ID;
        panel.className = 'liteTvNextCard' + (jeContent ? '' : ' liteTvPauseFloating');
        ['click', 'pointerdown', 'pointerup', 'mousedown', 'mouseup', 'touchstart', 'touchend'].forEach(function (evt) {
            panel.addEventListener(evt, function (e) { e.stopPropagation(); });
        });

        var eyebrow = document.createElement('div');
        eyebrow.className = 'liteTvEyebrow';
        eyebrow.textContent = 'Du siehst gerade';
        panel.appendChild(eyebrow);

        var name = document.createElement('div');
        name.className = 'liteTvNextName';
        name.textContent = '📺 ' + (tuned.channelName || 'TV-Sender');
        panel.appendChild(name);

        var meta = document.createElement('div');
        meta.className = 'liteTvPauseMeta';
        panel.appendChild(meta);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvNextButtons';
        panel.appendChild(buttons);

        var bingeBtn = null;
        var scheduleBtn = makeButton('Programm folgen', 'liteTvPill', function () {
            if (tuned) {
                tuned.mode = 'schedule';
            }
            refresh();
        });

        function refresh() {
            if (!tuned) {
                return;
            }
            var binging = tuned.mode === 'binge';
            scheduleBtn.classList.toggle('liteTvPillActive', !binging);
            if (bingeBtn) {
                bingeBtn.classList.toggle('liteTvPillActive', binging);
            }
        }

        if (tuned.currentSeriesId) {
            bingeBtn = makeButton('Serie weiterschauen', 'liteTvPill', function () {
                if (tuned) {
                    tuned.mode = 'binge';
                }
                refresh();
            });
            buttons.appendChild(bingeBtn);
            // The schedule/binge toggle only exists when there is a series to binge.
            buttons.appendChild(scheduleBtn);
        }

        buttons.appendChild(makeButton('↺ Von Anfang an', 'liteTvPill', function () {
            if (!tuned) {
                return;
            }
            tuned.mode = 'offschedule';
            chainInProgress = true;
            removePausePanel();
            playItem(tuned.currentItemId, 0).catch(function (err) {
                console.warn('liteTv: restart failed', err);
            });
        }));

        buttons.appendChild(makeButton('Sender verlassen', 'liteTvPill', function () {
            removePausePanel();
            untune();
        }));

        refresh();
        host.appendChild(panel);

        apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=8').then(function (now) {
            if (!tuned || !document.getElementById(PAUSE_PANEL_ID)) {
                return;
            }
            meta.innerHTML = '';
            var next = resolveScheduleNext(now, tuned.currentItemId);
            function line(prefix, text) {
                var el = document.createElement('div');
                var time = document.createElement('span');
                time.className = 'liteTvEpgTime';
                time.textContent = prefix;
                el.appendChild(time);
                el.appendChild(document.createTextNode(text));
                meta.appendChild(el);
            }
            var current = now.Current.ItemId === tuned.currentItemId ? now.Current : null;
            if (current) {
                line('Jetzt', (current.SeriesName ? current.SeriesName + ': ' : '') + current.Name);
            }
            if (next.program) {
                line('Danach', (next.program.SeriesName ? next.program.SeriesName + ': ' : '') + next.program.Name);
            }
        }).catch(function () { });
    }

    // ------------------------------------------------------- end-of-item logic

    function stopWatcher() {
        if (watchTimer) {
            clearInterval(watchTimer);
            watchTimer = null;
        }
    }

    // Reads the start of the item's outro/credits from the Media Segments API
    // (Jellyfin 10.11+). Returns the earliest outro start in seconds, or null when
    // the item has no outro segment.
    function fetchOutroStart(itemId) {
        return apiGet('MediaSegments/' + itemId + '?includeSegmentTypes=Outro').then(function (result) {
            var items = (result && result.Items) || [];
            var earliest = null;
            for (var i = 0; i < items.length; i++) {
                if (items[i].Type === 'Outro' && typeof items[i].StartTicks === 'number') {
                    var seconds = items[i].StartTicks / 10000000;
                    if (earliest === null || seconds < earliest) {
                        earliest = seconds;
                    }
                }
            }
            return earliest;
        }).catch(function () { return null; });
    }

    function startWatcher() {
        stopWatcher();
        // The watcher runs continuously for the whole tuned session, not just one
        // item. Jellyfin swaps the media in place on a chained PlayNow without
        // firing another 'viewshow', so a per-item watcher that stopped after
        // advancing would never restart and the channel would halt after one item.
        // Instead this watcher re-arms itself whenever the current item changes.
        var watchedItemId = null;
        var overlayShown = false;
        var fired = false;
        var settled = false;
        var segmentsRequested = false;
        var advancePoint = null; // seconds into the item at which to advance
        var nextInfo = null; // { schedule: ProgramDto|null, binge: {Id, Name}|null }

        function armForItem(itemId) {
            watchedItemId = itemId;
            overlayShown = false;
            fired = false;
            settled = false;
            segmentsRequested = false;
            advancePoint = null;
            nextInfo = null;
            removeOverlay(NEXT_OVERLAY_ID);
        }

        watchTimer = setInterval(function () {
            if (!tuned) {
                stopWatcher();
                return;
            }
            // A new item took over (the previous one advanced, or a restart/binge
            // switched items): reset the end-of-item state for it.
            if (tuned.currentItemId !== watchedItemId) {
                armForItem(tuned.currentItemId);
            }
            var video = document.querySelector('#videoOsdPage video') || document.querySelector('video');
            if (!video || !video.duration || isNaN(video.duration)) {
                return;
            }

            if (video.paused && !video.ended) {
                ensurePausePanel();
            } else {
                removePausePanel();
            }

            var remaining = video.duration - video.currentTime;

            // A freshly-started item reports plenty of remaining time; until we have
            // seen that, the <video> may still be the previous, already-ended item
            // lingering through the hand-off. Firing the follow-up on that stale
            // element would skip past the item that just started. So only arm the
            // end-of-item logic once the current item is genuinely playing.
            if (!video.ended && remaining > 2) {
                settled = true;
                // The item is genuinely playing, so any transition is complete: clear
                // the chain guard even when Jellyfin swapped the media in place without
                // a 'viewshow' (which is what would otherwise reset it). Otherwise
                // leaving the channel later would not be detected and the tuned state
                // would leak.
                chainInProgress = false;
            }
            if (!settled) {
                return;
            }

            // Resolve, once per item, where to advance: the start of the outro/credits
            // from the Media Segments API when the item has one, otherwise a fixed
            // margin before the real end. Either way the tail credits are skipped.
            if (!segmentsRequested) {
                segmentsRequested = true;
                advancePoint = video.duration - NEXT_ADVANCE_SECONDS;
                var segItemId = tuned.currentItemId;
                var segDuration = video.duration;
                fetchOutroStart(segItemId).then(function (outroSeconds) {
                    if (tuned && tuned.currentItemId === segItemId
                        && outroSeconds !== null && outroSeconds > 2 && outroSeconds < segDuration - 1) {
                        advancePoint = outroSeconds;
                    }
                });
            }

            var untilAdvance = advancePoint - video.currentTime;

            // Seeking back out of the window re-arms the overlay for the next approach.
            if (overlayShown && untilAdvance > NEXT_OVERLAY_WINDOW_SECONDS + 10) {
                overlayShown = false;
                removeOverlay(NEXT_OVERLAY_ID);
            }

            if (untilAdvance <= NEXT_OVERLAY_WINDOW_SECONDS && !overlayShown) {
                overlayShown = true;
                prepareNext().then(function (info) {
                    nextInfo = info;
                    if (tuned && info) {
                        showNextOverlay(info, Math.max(1, Math.floor(untilAdvance)));
                    }
                });
            }

            if ((untilAdvance <= 0 || video.ended) && !fired) {
                // Do not stop the watcher: it keeps running so the next item, which
                // Jellyfin swaps in without a 'viewshow', is picked up and advanced
                // in turn. playNext updates tuned.currentItemId, which re-arms us.
                fired = true;
                playNext(nextInfo);
            }
        }, 500);
    }

    // Determines the follow-up program relative to the item the viewer is actually
    // watching. The viewer may be ahead of the wall-clock schedule (skipping is
    // allowed), so the reference item is searched in the lineup: the follow-up is
    // whatever comes after it. Only when the reference item is no longer in the
    // lineup (the clock moved past it, or after a binge detour) do we rejoin the
    // live position.
    function resolveScheduleNext(now, referenceItemId) {
        var lineup = [now.Current].concat(now.Upcoming || []);
        for (var i = 0; i < lineup.length; i++) {
            if (lineup[i].ItemId === referenceItemId && i + 1 < lineup.length) {
                // Reference item has not fully aired yet per the clock, so the
                // viewer finished it early: its follow-up starts from the top.
                return { program: lineup[i + 1], offsetTicks: 0, live: false };
            }
        }
        return { program: now.Current, offsetTicks: now.OffsetTicks, live: true };
    }

    function prepareNext() {
        if (!tuned) {
            return Promise.resolve(null);
        }
        var schedulePromise = apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=8').then(function (now) {
            return resolveScheduleNext(now, tuned.currentItemId).program;
        }).catch(function () { return null; });

        var bingePromise = Promise.resolve(null);
        if (tuned.currentSeriesId) {
            bingePromise = window.ApiClient.getEpisodes(tuned.currentSeriesId, {
                userId: window.ApiClient.getCurrentUserId(),
                fields: 'Id,Name'
            }).then(function (result) {
                var episodes = (result && result.Items) || [];
                for (var i = 0; i < episodes.length; i++) {
                    if (episodes[i].Id === tuned.currentItemId && i + 1 < episodes.length) {
                        return episodes[i + 1];
                    }
                }
                return null;
            }).catch(function () { return null; });
        }

        return Promise.all([schedulePromise, bingePromise]).then(function (results) {
            return { schedule: results[0], binge: results[1] };
        });
    }

    function showNextOverlay(info, countdownSeconds) {
        ensureStyle();
        removeOverlay(NEXT_OVERLAY_ID);

        var overlay = document.createElement('div');
        overlay.id = NEXT_OVERLAY_ID;

        var card = document.createElement('div');
        card.className = 'liteTvNextCard';
        overlay.appendChild(card);

        var eyebrow = document.createElement('div');
        eyebrow.className = 'liteTvEyebrow';
        eyebrow.textContent = 'Als Nächstes';
        card.appendChild(eyebrow);

        var name = document.createElement('div');
        name.className = 'liteTvNextName';
        card.appendChild(name);

        var countdownText = document.createElement('div');
        countdownText.className = 'liteTvCountdownText';
        card.appendChild(countdownText);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvNextButtons';
        card.appendChild(buttons);

        var bar = document.createElement('div');
        bar.className = 'liteTvCountdownBar';
        var barFill = document.createElement('div');
        barFill.style.width = '100%';
        bar.appendChild(barFill);
        card.appendChild(bar);

        var totalSeconds = countdownSeconds;
        var secondsLeft = countdownSeconds;
        var scheduleBtn = null;
        var bingeBtn = null;
        // Without a series to continue there is no real choice; the same goes for
        // when the schedule's next program IS the next episode of this series.
        // Then show only what comes next, no buttons.
        var hasChoice = !!(info.binge && tuned && tuned.currentSeriesId
            && (!info.schedule || info.schedule.ItemId !== info.binge.Id));

        function scheduleName() {
            if (!info.schedule) {
                return 'Programm';
            }
            return info.schedule.SeriesName
                ? info.schedule.SeriesName + ': ' + info.schedule.Name
                : info.schedule.Name;
        }

        function update() {
            var binging = tuned && tuned.mode === 'binge' && info.binge;
            name.textContent = binging ? info.binge.Name : scheduleName();
            countdownText.textContent = secondsLeft > 0 ? 'startet in ' + secondsLeft + ' Sekunden' : 'startet gleich';
            barFill.style.width = Math.max(0, (secondsLeft / totalSeconds) * 100).toFixed(1) + '%';
            if (scheduleBtn) {
                scheduleBtn.classList.toggle('liteTvPillActive', !binging);
            }
            if (bingeBtn) {
                bingeBtn.classList.toggle('liteTvPillActive', !!binging);
            }
        }

        if (hasChoice) {
            bingeBtn = makeButton('Serie weiterschauen', 'liteTvPill', function () {
                if (tuned) {
                    tuned.mode = 'binge';
                }
                update();
            });
            buttons.appendChild(bingeBtn);

            scheduleBtn = makeButton('Programm folgen', 'liteTvPill', function () {
                if (tuned) {
                    tuned.mode = 'schedule';
                }
                update();
            });
            buttons.appendChild(scheduleBtn);
        } else {
            buttons.style.display = 'none';
        }

        update();

        var countdown = setInterval(function () {
            secondsLeft--;
            if (secondsLeft < 0 || !document.getElementById(NEXT_OVERLAY_ID)) {
                clearInterval(countdown);
                return;
            }
            update();
        }, 1000);

        getOsdContainer().appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');
    }

    function playNext(info) {
        removeOverlay(NEXT_OVERLAY_ID);
        if (!tuned) {
            return;
        }

        chainInProgress = true;
        if (tuned.mode === 'binge' && info && info.binge) {
            tuned.currentItemId = info.binge.Id;
            playItem(info.binge.Id, 0).catch(function (err) {
                console.warn('liteTv: binge next failed', err);
            });
            return;
        }

        // Follow the schedule. The just-ended item is the reference: if the viewer
        // skipped ahead and finished early, the follow-up program starts from the
        // beginning (running ahead of the live schedule); otherwise we rejoin the
        // live position.
        tuned.mode = 'schedule';
        var endedItemId = tuned.currentItemId;
        apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=8').then(function (now) {
            var next = resolveScheduleNext(now, endedItemId);
            tuned.currentItemId = next.program.ItemId;
            tuned.currentSeriesId = next.program.SeriesId || null;
            return playItem(next.program.ItemId, next.offsetTicks);
        }).catch(function (err) {
            console.warn('liteTv: schedule next failed', err);
            untune();
        });
    }

    // ------------------------------------------------------------------ guide

    function cardImageUrl(program) {
        if (!program) {
            return null;
        }
        var id = program.SeriesId || program.ItemId;
        try {
            return window.ApiClient.getUrl('Items/' + id + '/Images/Backdrop/0', { maxWidth: 640, quality: 80 });
        } catch (e) {
            return null;
        }
    }

    function buildChannelCard(channel) {
        var card = document.createElement('div');
        card.className = 'liteTvCard';

        var imageUrl = cardImageUrl(channel.Now);
        if (imageUrl) {
            card.style.backgroundImage = 'url("' + imageUrl + '")';
        }

        var shade = document.createElement('div');
        shade.className = 'liteTvCardShade';
        card.appendChild(shade);

        var chip = document.createElement('div');
        chip.className = 'liteTvChannelChip';
        chip.textContent = '📺 ' + channel.Name;
        card.appendChild(chip);

        var body = document.createElement('div');
        body.className = 'liteTvCardBody';
        card.appendChild(body);

        var now = document.createElement('div');
        now.className = 'liteTvNow';
        if (channel.Now) {
            now.textContent = (channel.Now.SeriesName ? channel.Now.SeriesName + ': ' : '') + channel.Now.Name;
        } else {
            now.textContent = 'Sendepause';
        }
        body.appendChild(now);

        if (channel.Now) {
            var start = new Date(channel.Now.StartUtc).getTime();
            var end = new Date(channel.Now.EndUtc).getTime();
            var pct = end > start ? Math.min(100, Math.max(0, ((Date.now() - start) / (end - start)) * 100)) : 0;
            var progress = document.createElement('div');
            progress.className = 'liteTvProgress';
            var barEl = document.createElement('div');
            barEl.style.width = pct.toFixed(1) + '%';
            progress.appendChild(barEl);
            body.appendChild(progress);
        }

        if (channel.Next) {
            var next = document.createElement('div');
            next.className = 'liteTvNext';
            next.textContent = 'Danach ' + formatTime(channel.Next.StartUtc) + ' · '
                + (channel.Next.SeriesName ? channel.Next.SeriesName + ': ' : '') + channel.Next.Name;
            body.appendChild(next);
        }

        card.addEventListener('click', function () {
            tuneIn(channel.Id);
        });
        return card;
    }

    function homeRowSignature(channels) {
        return JSON.stringify(channels.map(function (channel) {
            return { Id: channel.Id, Now: channel.Now, Next: channel.Next };
        }));
    }

    // Parks the TV row inside the stock home sections flow (.homeSectionsContainer)
    // as its last child, so it shares the same vertical rhythm as the other rows.
    // That container renders asynchronously after us, so until it exists the row is
    // appended to the page and the observer migrates it in once it appears. Appending
    // to the page directly (below #homeTab) is what left an oversized gap above it.
    function placeHomeRow(page, section) {
        var host = page.querySelector('.homeSectionsContainer') || page;
        if (section.parentNode !== host || host.lastElementChild !== section) {
            host.appendChild(section);
        }
    }

    function observeHomeRow(page, section) {
        if (homeRowObserver) {
            homeRowObserver.disconnect();
        }
        homeRowObserver = new MutationObserver(function () {
            placeHomeRow(page, section);
        });
        homeRowObserver.observe(page, { childList: true, subtree: true });
    }

    function renderHomeRow(page) {
        apiGet('LiteTv/Channels').then(function (guide) {
            if (!guide.EnableWebUi || !guide.ShowHomeRow || !guide.Channels.length) {
                return;
            }
            ensureStyle();
            var existing = document.getElementById(HOME_ROW_ID);

            var signature = homeRowSignature(guide.Channels);
            if (signature === lastHomeRowGuide && existing && existing.parentNode) {
                // Data unchanged: leave the cards alone, but keep the row parked in
                // the sections flow and pinned there.
                placeHomeRow(page, existing);
                observeHomeRow(page, existing);
                return;
            }
            lastHomeRowGuide = signature;

            if (existing && existing.parentNode) {
                existing.parentNode.removeChild(existing);
            }

            var section = document.createElement('div');
            section.id = HOME_ROW_ID;
            section.className = 'verticalSection';
            var heading = document.createElement('h2');
            heading.className = 'sectionTitle';
            heading.textContent = 'TV-Sender';
            section.appendChild(heading);

            var cards = document.createElement('div');
            cards.className = 'liteTvCards';
            guide.Channels.forEach(function (channel) {
                cards.appendChild(buildChannelCard(channel));
            });
            section.appendChild(cards);

            placeHomeRow(page, section);
            observeHomeRow(page, section);
        }).catch(function (err) {
            console.debug('liteTv: guide not available', err);
        });
    }

    function closeGuide() {
        removeOverlay(GUIDE_ID);
    }

    function openGuide() {
        closeGuide();
        ensureStyle();
        apiGet('LiteTv/Channels').then(function (guide) {
            var backdrop = document.createElement('div');
            backdrop.id = GUIDE_ID;
            backdrop.addEventListener('click', function (e) {
                if (e.target === backdrop) {
                    closeGuide();
                }
            });

            var panel = document.createElement('div');
            panel.className = 'liteTvPanel';
            backdrop.appendChild(panel);

            var heading = document.createElement('h2');
            heading.textContent = '📺 TV-Sender';
            panel.appendChild(heading);

            if (!guide.Channels.length) {
                var empty = document.createElement('div');
                empty.textContent = 'Keine Sender konfiguriert. Sender werden im Dashboard unter Plugins → LiteTV Channels angelegt.';
                panel.appendChild(empty);
            }

            guide.Channels.forEach(function (channel) {
                var row = document.createElement('div');
                row.className = 'liteTvGuideChannel';

                var head = document.createElement('div');
                head.className = 'liteTvGuideHead';
                var name = document.createElement('div');
                name.className = 'liteTvGuideName';
                name.textContent = channel.Name;
                head.appendChild(name);

                var actions = document.createElement('div');
                actions.className = 'liteTvGuideActions';

                var devices = document.createElement('div');
                devices.className = 'liteTvDevices';

                actions.appendChild(makeButton('▶ Ansehen', 'liteTvPill liteTvPillPrimary', function () {
                    tuneIn(channel.Id);
                }));

                actions.appendChild(makeButton('Auf Gerät…', 'liteTvPill', function () {
                    if (devices.style.display === 'block') {
                        devices.style.display = 'none';
                        return;
                    }
                    devices.style.display = 'block';
                    devices.innerHTML = '';
                    window.ApiClient.getSessions().then(function (sessions) {
                        var ownDeviceId = window.ApiClient.deviceId();
                        var targets = (sessions || []).filter(function (s) {
                            return s.DeviceId !== ownDeviceId
                                && s.SupportsRemoteControl !== false;
                        });
                        if (!targets.length) {
                            var none = document.createElement('div');
                            none.textContent = 'Keine anderen aktiven Geräte gefunden.';
                            devices.appendChild(none);
                            return;
                        }
                        targets.forEach(function (s) {
                            devices.appendChild(makeButton(
                                (s.DeviceName || s.Client || 'Gerät') + (s.UserName ? ' – ' + s.UserName : ''),
                                'liteTvPill',
                                function () {
                                    apiPost('LiteTv/Channels/' + channel.Id + '/PlayOn/' + encodeURIComponent(s.Id)).then(function () {
                                        closeGuide();
                                    }).catch(function (err) {
                                        console.warn('liteTv: play on device failed', err);
                                    });
                                }));
                        });
                    });
                }));

                head.appendChild(actions);
                row.appendChild(head);

                var epg = document.createElement('div');
                epg.className = 'liteTvEpg';
                function epgLine(prefix, program) {
                    var line = document.createElement('div');
                    var time = document.createElement('span');
                    time.className = 'liteTvEpgTime';
                    time.textContent = prefix;
                    line.appendChild(time);
                    line.appendChild(document.createTextNode(
                        (program.SeriesName ? program.SeriesName + ': ' : '') + program.Name));
                    return line;
                }
                if (channel.Now) {
                    epg.appendChild(epgLine('Jetzt', channel.Now));
                }
                if (channel.Next) {
                    epg.appendChild(epgLine(formatTime(channel.Next.StartUtc), channel.Next));
                }
                row.appendChild(epg);
                row.appendChild(devices);
                panel.appendChild(row);
            });

            document.body.appendChild(backdrop);
            void backdrop.offsetWidth;
            backdrop.classList.add('liteTvVisible');
        }).catch(function (err) {
            console.debug('liteTv: guide not available', err);
        });
    }

    function ensureHeaderButton() {
        if (document.getElementById('liteTvHeaderBtn')) {
            return;
        }
        var headerRight = document.querySelector('.headerRight');
        if (!headerRight) {
            return;
        }
        apiGet('LiteTv/Channels').then(function (guide) {
            if (!guide.EnableWebUi || !guide.ShowHeaderButton || document.getElementById('liteTvHeaderBtn')) {
                return;
            }
            var btn = document.createElement('button');
            btn.id = 'liteTvHeaderBtn';
            btn.type = 'button';
            btn.className = 'headerButton paper-icon-button-light';
            btn.title = 'TV-Sender';
            btn.textContent = '📺';
            btn.addEventListener('click', openGuide);
            headerRight.insertBefore(btn, headerRight.firstChild);
        }).catch(function () { });
    }

    // ------------------------------------------------------------- page hooks

    function isVideoOsd(e) {
        if (e && e.detail && typeof e.detail.type === 'string') {
            return e.detail.type === 'video-osd';
        }
        var page = e && e.target;
        return !!(page && page.id === 'videoOsdPage') || !!document.querySelector('#videoOsdPage:not(.hide)');
    }

    function isHome(e) {
        if (e && e.detail && typeof e.detail.type === 'string') {
            return e.detail.type === 'home';
        }
        var page = e && e.target;
        return !!(page && page.id === 'indexPage');
    }

    document.addEventListener('viewshow', function (e) {
        if (!window.ApiClient) {
            return;
        }
        ensureHeaderButton();

        if (isVideoOsd(e)) {
            if (tuned) {
                chainInProgress = false;
                startWatcher();
            }
            return;
        }

        if (isHome(e) && e.target) {
            renderHomeRow(e.target);
        }

        // Landing anywhere that is not the video OSD means the viewer has left the
        // channel, unless we are mid-chain (the OSD briefly hides while switching
        // items, which can surface the home page for a moment). This includes the
        // home page, so returning home does not leave the session marked tuned.
        if (tuned && !chainInProgress) {
            untune();
        }
    });

    document.addEventListener('viewhide', function (e) {
        if (e && e.target && e.target.id === 'videoOsdPage') {
            stopWatcher();
            removeOverlay(TUNE_OVERLAY_ID);
            removeOverlay(NEXT_OVERLAY_ID);
            removePausePanel();
        }
    });
})();
