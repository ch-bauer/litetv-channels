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
    var INTERSTITIAL_ID = 'liteTvInterstitial';
    var TUNED_BODY_CLASS = 'liteTvTuned';
    var NEXT_OVERLAY_WINDOW_SECONDS = 45;
    // Programs play out in full, credits included. The hand-off still sits a second
    // short of the very end: once the video actually ends Jellyfin tears the player
    // down and routes back to the previous page, which would turn the seamless
    // in-place swap into a visible detour through the home screen.
    var END_MARGIN_SECONDS = 1;

    // Tuned state for this browser tab. mode: 'schedule' | 'binge'
    var tuned = null;
    var watchTimer = null;
    // Set while the "Als Nächstes" overlay is up: feeds it the live remaining time so
    // the countdown follows the player instead of the wall clock.
    var nextOverlayCountdown = null;
    // Set once the choice on that overlay has been acted on, so the buttons can go: the
    // follow-up is queued in the player by then and there is no taking it back.
    var nextOverlayCommit = null;
    var homeRowObserver = null;
    var lastHomeRowGuide = null;
    var interstitialTimer = null;

    // The UI options, fetched once. They come off the same endpoint as the guide, but the
    // guide's contents move with the clock and these do not, so they are kept apart: the
    // home row asks fresh every time, everything else asks this.
    var flagsPromise = null;

    function getFlags() {
        if (!flagsPromise) {
            flagsPromise = apiGet('LiteTv/Channels').then(function (guide) {
                return {
                    enabled: !!guide.EnableWebUi,
                    hideNativeLiveTv: !!guide.HideNativeLiveTvSections,
                    shieldBingedEpisodes: guide.ShieldBingedEpisodes !== false
                };
            }).catch(function () {
                // Shielding stays on in the fallback: not knowing is not a reason to start
                // writing channel viewing to the account.
                return { enabled: false, hideNativeLiveTv: false, shieldBingedEpisodes: true };
            });
        }
        return flagsPromise;
    }

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

    // The artwork for a programme. The server names an image only where one actually
    // exists - on the item itself or on its series - so nothing here has to guess, and a
    // programme whose library entry has no picture simply gets none instead of a broken
    // rectangle. 'poster' is the portrait cover, 'wide' the landscape one.
    function programImage(program, shape, maxWidth) {
        if (!program) {
            return null;
        }
        var id = shape === 'poster' ? program.PosterItemId : program.BackdropItemId;
        var type = shape === 'poster' ? program.PosterType : program.BackdropType;
        if (!id || !type) {
            return null;
        }
        try {
            return window.ApiClient.getUrl('Items/' + id + '/Images/' + type, {
                maxWidth: maxWidth || 480,
                quality: 85
            });
        } catch (e) {
            return null;
        }
    }

    // Draws a programme's artwork into a container, and leaves the container alone when
    // there is none - an empty frame says less than no frame at all.
    function addImage(host, program, shape, maxWidth, className) {
        var url = programImage(program, shape, maxWidth);
        if (!url) {
            return null;
        }
        var img = document.createElement('img');
        img.className = className;
        img.src = url;
        // Eagerly, never lazily. These images sit in overlays that are laid out only when
        // they are shown, so until the picture arrives the element has no height - and an
        // element with no height is one the browser does not consider worth loading yet.
        // The picture then never appears at all. There are only ever a handful of them.
        img.loading = 'eager';
        img.alt = '';
        // A picture the server offered but the client cannot fetch must not leave a
        // broken-image glyph sitting in the middle of the overlay.
        img.addEventListener('error', function () {
            if (img.parentNode) {
                img.parentNode.removeChild(img);
            }
        });
        host.appendChild(img);
        return img;
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

            /* ---- Jellyfin's own Live TV home rows, when the setting asks for it ---- */
            '.liteTvHiddenSection { display: none !important; }' +

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

            /* --------------------------------------------------- guide grid ---- */
            /* A time grid, the way a channel guide has always been drawn: one row per */
            /* channel, time running left to right, every programme as wide as it is long. */
            '#' + GUIDE_ID + ' .liteTvPanelWide { width: min(78em, 96vw); }' +
            '#' + GUIDE_ID + ' .liteTvGuideBar { display: flex; align-items: center; gap: 0.5em; margin-bottom: 0.9em; flex-wrap: wrap; }' +
            '#' + GUIDE_ID + ' .liteTvGuideBar h2 { margin: 0; flex-grow: 1; }' +
            /* The bar is gone on purpose: the arrows move the guide, and on a touch screen it
               swipes. A scrollbar under a time grid invites dragging it to a position
               rather than to a time, which is not how anyone reads a schedule. */
            '#' + GUIDE_ID + ' .liteTvGrid {' +
            '  overflow-x: auto; overflow-y: visible; scroll-behavior: smooth;' +
            '  scrollbar-width: none; -ms-overflow-style: none;' +
            '}' +
            '#' + GUIDE_ID + ' .liteTvGrid::-webkit-scrollbar { display: none; }' +
            '#' + GUIDE_ID + ' .liteTvAxis { display: flex; position: sticky; top: 0; z-index: 3; background: rgba(24, 27, 33, 0.97); }' +
            '#' + GUIDE_ID + ' .liteTvRow { display: flex; margin-top: 0.35em; }' +
            '#' + GUIDE_ID + ' .liteTvRowName, #' + GUIDE_ID + ' .liteTvAxisSpacer {' +
            '  position: sticky; left: 0; z-index: 2; flex: 0 0 9.5em; width: 9.5em;' +
            '  background: rgba(24, 27, 33, 0.97); padding-right: 0.6em; box-sizing: border-box;' +
            '}' +
            '#' + GUIDE_ID + ' .liteTvRowName { display: flex; flex-direction: column; justify-content: center; gap: 0.3em; }' +
            '#' + GUIDE_ID + ' .liteTvRowName .liteTvGuideName { font-size: 1em; cursor: pointer; }' +
            '#' + GUIDE_ID + ' .liteTvRowName .liteTvPill { padding: 0.2em 0.7em; font-size: 0.75em; align-self: flex-start; }' +
            '#' + GUIDE_ID + ' .liteTvTrack { position: relative; height: 3.4em; }' +
            '#' + GUIDE_ID + ' .liteTvAxis .liteTvTrack { height: 1.8em; }' +
            '#' + GUIDE_ID + ' .liteTvTick {' +
            '  position: absolute; top: 0; bottom: 0; border-left: 1px solid rgba(255,255,255,0.12);' +
            '  padding-left: 0.4em; font-size: 0.78em; opacity: 0.75; font-variant-numeric: tabular-nums;' +
            '}' +
            '#' + GUIDE_ID + ' .liteTvProg {' +
            '  position: absolute; top: 0; bottom: 0; overflow: hidden;' +
            '  border-radius: 0.4em; border: 1px solid rgba(255,255,255,0.12);' +
            '  background: rgba(255,255,255,0.07); padding: 0.4em 0.6em; box-sizing: border-box;' +
            '  font-size: 0.82em; line-height: 1.25; cursor: default;' +
            '}' +
            '#' + GUIDE_ID + ' .liteTvProgOn { background: rgba(0,164,220,0.28); border-color: rgba(77,208,255,0.55); cursor: pointer; }' +
            '#' + GUIDE_ID + ' .liteTvProgOn:hover { background: rgba(0,164,220,0.42); }' +
            '#' + GUIDE_ID + ' .liteTvProgFill { background: repeating-linear-gradient(135deg, rgba(255,255,255,0.05) 0 0.5em, rgba(255,255,255,0.02) 0.5em 1em); opacity: 0.85; }' +
            '#' + GUIDE_ID + ' .liteTvProgDark { background: rgba(255,255,255,0.02); opacity: 0.55; }' +
            '#' + GUIDE_ID + ' .liteTvProg { display: flex; align-items: stretch; gap: 0.5em; }' +
            '#' + GUIDE_ID + ' .liteTvProgArt { flex: 0 0 auto; width: auto; height: 100%; border-radius: 0.2em; object-fit: cover; }' +
            '#' + GUIDE_ID + ' .liteTvProgText { min-width: 0; align-self: center; }' +
            '#' + GUIDE_ID + ' .liteTvProgTitle { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '#' + GUIDE_ID + ' .liteTvProgTime { opacity: 0.7; font-size: 0.9em; font-variant-numeric: tabular-nums; }' +
            '#' + GUIDE_ID + ' .liteTvNowLine { position: absolute; top: 0; bottom: 0; width: 2px; background: #ff4d4f; z-index: 1; pointer-events: none; }' +

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
            /* A card with artwork puts the picture beside the words, and keeps the words the */
            /* same width they had without it so nothing reflows when a poster is missing. */
            '.liteTvNextCard.liteTvWithArt { display: flex; gap: 1em; align-items: flex-start; max-width: 34em; }' +
            '.liteTvWithArt .liteTvCardText { min-width: 17em; flex: 1 1 auto; }' +
            '.liteTvWithArt .liteTvArtSlot:empty { display: none; }' +
            '.liteTvArt { display: block; width: 7.5em; border-radius: 0.55em; box-shadow: 0 0.3em 1em rgba(0,0,0,0.5); }' +
            '.liteTvEyebrow { font-size: 0.7em; letter-spacing: 0.22em; text-transform: uppercase; opacity: 0.65; margin-bottom: 0.45em; }' +
            '.liteTvNextName { font-size: 1.18em; font-weight: 700; line-height: 1.3; margin-bottom: 0.2em; }' +
            '.liteTvCountdownText { font-size: 0.85em; opacity: 0.7; margin-bottom: 0.9em; font-variant-numeric: tabular-nums; }' +
            '.liteTvNextButtons { display: flex; gap: 0.55em; flex-wrap: wrap; }' +
            '.liteTvNextButtons .liteTvPill { pointer-events: auto; }' +
            '.liteTvCountdownBar { height: 0.2em; border-radius: 999px; background: rgba(255,255,255,0.16); margin-top: 1em; overflow: hidden; }' +
            '.liteTvCountdownBar > div { height: 100%; background: linear-gradient(90deg, #00a4dc, #4dd0ff); transition: width 1s linear; }' +

            /* ------------------------------------------------- interstitial ---- */
            '#' + INTERSTITIAL_ID + ' {' +
            '  position: fixed; inset: 0; z-index: 1150; display: flex;' +
            '  align-items: center; justify-content: center; background: #05070b;' +
            '  opacity: 0; transition: opacity 0.35s ease; font-size: clamp(11px, 1.1vw, 17px); color: #fff;' +
            '}' +
            '#' + INTERSTITIAL_ID + '.liteTvVisible { opacity: 1; }' +
            '#' + INTERSTITIAL_ID + ' .liteTvInterCard { width: min(64em, 92vw); text-align: left; }' +
            '#' + INTERSTITIAL_ID + ' .liteTvInterVideo {' +
            '  width: 100%; aspect-ratio: 16 / 9; border-radius: 0.7em; margin-bottom: 1.1em;' +
            '  background: #000; box-shadow: 0 0.6em 2.2em rgba(0,0,0,0.6);' +
            '}' +
            '#' + INTERSTITIAL_ID + ' .liteTvInterStill {' +
            '  width: 100%; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 0.7em;' +
            '  margin-bottom: 1.1em; box-shadow: 0 0.6em 2.2em rgba(0,0,0,0.6);' +
            '}' +

            /* -------------------------------------------------- pause panel ---- */
            '#' + PAUSE_PANEL_ID + ' { pointer-events: auto; font-size: clamp(11px, 1.1vw, 17px); }' +
            '#' + PAUSE_PANEL_ID + '.liteTvPauseFloating { position: absolute; left: 4%; bottom: 14%; z-index: 1000; margin: 0; }' +
            /* Inside Jellyfin Enhanced's pause screen everything is absolutely positioned; */
            /* top-right is free (logo/details/plot live on the left, the disc sits mid-right). */
            '#pause-screen-content #' + PAUSE_PANEL_ID + ' { position: absolute; right: 5vw; top: 7vh; margin: 0; z-index: 5; }' +
            '#' + PAUSE_PANEL_ID + ' .liteTvPauseChannel { font-size: 0.9em; opacity: 0.75; margin-bottom: 0.25em; }' +
            '#' + PAUSE_PANEL_ID + ' .liteTvPauseMeta { font-size: 0.9em; opacity: 0.85; line-height: 1.55; margin-bottom: 0.95em; }' +
            '#' + PAUSE_PANEL_ID + ' .liteTvPauseMeta .liteTvEpgTime { color: #4dd0ff; margin-right: 0.4em; font-variant-numeric: tabular-nums; }';
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- playback

    // A channel is played as a playlist, not as one programme handed to the player after
    // another. The schedule is fetched once, turned into a queue of the real library items
    // it names, and given to the player in a single command with a start position for the
    // first. From then on Jellyfin's own player moves through it: the gap between programmes
    // is a normal track change, leaving the channel is the ordinary back button, and skipping
    // is the player's own next-track. Chaining each item by remote-controlling our own
    // session is what made all three of those misbehave.
    //
    // The whole queue is shielded up front, in one call. Once the player is moving through
    // it by itself there is no later moment to arm anything: the first watch-state report of
    // the next programme arrives before any script here could react to it.
    var QUEUE_HOURS = 6;
    var EMPTY_GUID = '00000000000000000000000000000000';

    function playable(program) {
        return !!program.ItemId && program.ItemId !== EMPTY_GUID
            && (program.Kind === 'Program' || program.Kind === 'Interstitial');
    }

    function sendPlay(sessionId, itemIds, positionTicks, command) {
        var url = window.ApiClient.getUrl('Sessions/' + sessionId + '/Playing', {
            playCommand: command || 'PlayNow',
            itemIds: itemIds.join(','),
            startPositionTicks: Math.max(0, Math.round(positionTicks || 0))
        });
        return window.ApiClient.fetch({ url: url, type: 'POST' });
    }

    // Splits the schedule into the longest runs that contain no repeat.
    //
    // A run can be handed to the player in one command; a repeat cannot. The ids are
    // resolved through a lookup that returns each item once, so a command naming the same
    // film twice comes back one item short and the rest of the schedule shifts up by one.
    // Which matters here precisely because a channel loops: measured on this server, six
    // hours of one channel is 72 entries drawn from 8 distinct films. Cut at every repeat
    // it is 9 commands rather than 72.
    function queueRuns(ids) {
        var runs = [];
        var run = [];
        var seen = Object.create(null);
        for (var i = 0; i < ids.length; i++) {
            if (seen[ids[i]]) {
                runs.push(run);
                run = [];
                seen = Object.create(null);
            }
            run.push(ids[i]);
            seen[ids[i]] = true;
        }
        if (run.length) {
            runs.push(run);
        }
        return runs;
    }

    // Appends the rest of the schedule behind what is already playing. Sequential on
    // purpose: each command has to land before the next, or the queue ends up in whatever
    // order the requests happened to complete in.
    function appendToQueue(sessionId, runs, from) {
        if (from >= runs.length || !tuned || tuned.sessionId !== sessionId) {
            return Promise.resolve();
        }
        return sendPlay(sessionId, runs[from], 0, 'PlayLast').then(function () {
            return appendToQueue(sessionId, runs, from + 1);
        }, function (err) {
            // A short channel is better than a broken one: whatever made it this far still
            // plays, and the guide is re-read the next time the viewer tunes in.
            console.warn('liteTv: could not finish queueing the schedule', err);
        });
    }

    // The player's own transport controls, used rather than re-issuing playback ourselves.
    function nextTrack() {
        if (!tuned) {
            return Promise.resolve();
        }
        return apiPost('Sessions/' + tuned.sessionId + '/Playing/NextTrack').catch(function () { });
    }

    function seekToStart() {
        if (!tuned) {
            return Promise.resolve();
        }
        return apiPost('Sessions/' + tuned.sessionId + '/Playing/Seek?seekPositionTicks=0').catch(function () { });
    }

    function tuneIn(channelId) {
        closeGuide();
        return Promise.all([
            apiGet('LiteTv/Guide?hours=' + QUEUE_HOURS),
            apiGet('LiteTv/Channels/' + channelId + '/Now?upcoming=1'),
            getOwnSession()
        ]).then(function (results) {
            var guide = results[0];
            var now = results[1];
            var session = results[2];
            if (!session) {
                throw new Error('own session not found');
            }

            var row = (guide.Channels || []).filter(function (c) { return c.Id === channelId; })[0];
            var airings = row ? (row.Programs || []).filter(playable) : [];
            if (!airings.length) {
                // Nothing to queue: either the slot is between programmes, or the channel is
                // dark. The first is a gap to fill, the second is not something to sit in.
                if (now && now.Kind === 'Interstitial') {
                    showInterstitial(now, channelId);
                } else {
                    console.debug('liteTv: channel is off air');
                }
                return null;
            }

            var ids = airings.map(function (a) { return a.ItemId; });
            // The offset belongs to whatever is on now, which is what the guide window starts
            // with. If the schedule moved on between the two calls the queue simply starts at
            // the top of its first item - a second's difference, not a wrong one.
            var offset = (now && now.Current && now.Current.ItemId === ids[0]) ? now.OffsetTicks : 0;

            return apiPost('LiteTv/Tuned?sessionId=' + encodeURIComponent(session.Id)
                + '&channelId=' + channelId + '&itemIds=' + ids.join(','))
                .then(function () {
                    // The first programme starts straight away, at the live position.
                    return sendPlay(session.Id, [ids[0]], offset, 'PlayNow');
                }, function (err) {
                    // The server answers 409 when it could not shield the queue. Playing it
                    // then would write every programme into the watch history, so it is not
                    // played at all - a channel that does not start is a fault someone
                    // notices, a channel that rewrites their history is one they do not.
                    console.warn('liteTv: the channel could not be shielded, not tuning in', err);
                    throw err;
                })
                .then(function () {
                    tuned = {
                        channelId: channelId,
                        channelName: row.Name,
                        sessionId: session.Id,
                        queue: ids,
                        airings: airings,
                        index: 0,
                        mode: 'schedule',
                        currentItemId: ids[0],
                        currentName: null,
                        currentSeriesId: airings[0].SeriesId || null,
                        // Episodes already watched by carrying on with the series, so the
                        // schedule does not air them again when the queue reaches them.
                        bingedIds: [],
                        bingePlayingId: null
                    };
                    document.body.classList.add(TUNED_BODY_CLASS);
                    showTuneOverlay({ ChannelName: row.Name });
                    startWatcher();
                    // The rest is appended behind it while it plays.
                    appendToQueue(session.Id, queueRuns(ids.slice(1)), 0);
                });
        }).catch(function (err) {
            console.warn('liteTv: tune-in failed', err);
        });
    }

    // ------------------------------------------------- adopting channel playback

    // A channel can also be switched on from the "TV-Sender" entry in the library, which is
    // how every client that the injected UI never reaches does it. On the web that leaves the
    // viewer with the bare channel item: its metadata is the schedule text, it has no film to
    // show for itself, and the schedule is driven from the server, which cannot tell a seek
    // from the end of a programme. So the web client takes it over - it joins the same
    // channel at the same position through its own path, and everything the channel row
    // offers comes with it: the artwork and plot of what is actually on, the overlays, the
    // pause panel, and a hand-off that happens here rather than being pushed from outside.
    var adoptInFlight = false;

    function adoptChannelPlayback() {
        if (tuned || adoptInFlight) {
            return;
        }
        adoptInFlight = true;

        var attempts = 0;
        function done() {
            adoptInFlight = false;
        }

        function look() {
            attempts++;
            getOwnSession().then(function (session) {
                var playing = session && session.NowPlayingItem;
                if (!playing) {
                    // The session takes a moment to report what it started playing.
                    if (attempts < 6 && !tuned) {
                        setTimeout(look, 700);
                    } else {
                        done();
                    }
                    return;
                }
                // 404 for everything that is not one of ours, which is the common case.
                apiGet('LiteTv/Playing/' + playing.Id).then(function (info) {
                    done();
                    if (!info || !info.ChannelId || tuned) {
                        return;
                    }
                    tuneIn(info.ChannelId).then(function () {
                        // The OSD is already up, so no 'viewshow' will follow to start the
                        // watcher the way tuning in from the channel row does.
                        if (tuned) {
                            startWatcher();
                        }
                    });
                }).catch(done);
            }).catch(done);
        }

        look();
    }

    // ---------------------------------------------------------- interstitials

    // A slot that leaves time over before the next programme is what a real channel fills
    // with trailers. Trailers the library holds as files are scheduled by the server and
    // simply play; the far more common kind is one the library only knows the address of,
    // and those are embedded here, in the client, because nothing else can play them.
    function youtubeId(url) {
        var match = /(?:youtube\.com\/(?:watch\?(?:.*&)?v=|embed\/)|youtu\.be\/)([A-Za-z0-9_-]{11})/.exec(url || '');
        return match ? match[1] : null;
    }

    function clearInterstitial() {
        if (interstitialTimer) {
            clearInterval(interstitialTimer);
            interstitialTimer = null;
        }
        removeOverlay(INTERSTITIAL_ID);
    }

    /// Shows the gap: the trailer for what is about to start, and how long until it does.
    /// When it does, the channel is joined - which is what the viewer asked for by tuning in.
    function showInterstitial(now, channelId) {
        clearInterstitial();
        ensureStyle();

        var endMs = new Date(now.EndUtc).getTime();
        var overlay = document.createElement('div');
        overlay.id = INTERSTITIAL_ID;

        var card = document.createElement('div');
        card.className = 'liteTvInterCard';
        overlay.appendChild(card);

        var trailer = (now.Trailers || [])[0];
        var videoId = trailer ? youtubeId(trailer.Url) : null;
        if (videoId) {
            var frame = document.createElement('iframe');
            frame.className = 'liteTvInterVideo';
            frame.setAttribute('allow', 'autoplay; encrypted-media');
            frame.setAttribute('allowfullscreen', '');
            frame.setAttribute('frameborder', '0');
            frame.src = 'https://www.youtube.com/embed/' + videoId
                + '?autoplay=1&playsinline=1&rel=0&modestbranding=1&controls=0';
            card.appendChild(frame);
        } else {
            // No trailer to run: the still of what is coming up is better than a black gap.
            addImage(card, now.NextProgram, 'wide', 960, 'liteTvInterStill');
        }

        var eyebrow = document.createElement('div');
        eyebrow.className = 'liteTvEyebrow';
        eyebrow.textContent = 'Werbepause';
        card.appendChild(eyebrow);

        var title = document.createElement('div');
        title.className = 'liteTvNextName';
        title.textContent = now.NextProgram
            ? 'Gleich: ' + (now.NextProgram.SeriesName ? now.NextProgram.SeriesName + ': ' : '') + now.NextProgram.Name
            : 'Gleich geht es weiter';
        card.appendChild(title);

        var countdown = document.createElement('div');
        countdown.className = 'liteTvCountdownText';
        card.appendChild(countdown);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvNextButtons';
        buttons.appendChild(makeButton('Sender verlassen', 'liteTvPill', function () {
            clearInterstitial();
            leaveChannel();
        }));
        card.appendChild(buttons);

        function tick() {
            var remaining = Math.max(0, Math.round((endMs - Date.now()) / 1000));
            countdown.textContent = remaining > 0
                ? 'Weiter in ' + Math.floor(remaining / 60) + ':' + ('0' + (remaining % 60)).slice(-2)
                : 'Es geht los …';
            if (remaining <= 0) {
                clearInterstitial();
                tuneIn(channelId);
            }
        }

        tick();
        interstitialTimer = setInterval(tick, 1000);
        document.body.appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');
    }

    // What the "Sender verlassen" buttons do. The player holds the whole schedule now, so
    // walking away from the channel without telling it would leave it quietly working
    // through the rest of the evening.
    function leaveChannel() {
        var sessionId = tuned && tuned.sessionId;
        untune();
        if (sessionId) {
            apiPost('Sessions/' + sessionId + '/Playing/Stop').catch(function () { });
        }
    }

    function untune() {
        clearInterstitial();
        if (!tuned) {
            return;
        }
        var sessionId = tuned.sessionId;
        tuned = null;
        stopWatcher();
        document.body.classList.remove(TUNED_BODY_CLASS);
        removeOverlay(TUNE_OVERLAY_ID);
        clearNextOverlay();
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
            // A seek, not a re-issued playback: the programme is already the track the
            // player is on, so starting it again is simply going back to its beginning.
            seekToStart();
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
        panel.className = 'liteTvNextCard liteTvWithArt' + (jeContent ? '' : ' liteTvPauseFloating');
        ['click', 'pointerdown', 'pointerup', 'mousedown', 'mouseup', 'touchstart', 'touchend'].forEach(function (evt) {
            panel.addEventListener(evt, function (e) { e.stopPropagation(); });
        });

        // The poster of what is on air. A pause screen that names a channel and nothing else
        // is a caption; this is what makes it a picture of what you stopped in the middle of.
        var art = document.createElement('div');
        art.className = 'liteTvArtSlot';
        panel.appendChild(art);

        var text = document.createElement('div');
        text.className = 'liteTvCardText';
        panel.appendChild(text);

        var eyebrow = document.createElement('div');
        eyebrow.className = 'liteTvEyebrow';
        eyebrow.textContent = 'Du siehst gerade';
        text.appendChild(eyebrow);

        var channelLine = document.createElement('div');
        channelLine.className = 'liteTvPauseChannel';
        channelLine.textContent = '📺 ' + (tuned.channelName || 'TV-Sender');
        text.appendChild(channelLine);

        var name = document.createElement('div');
        name.className = 'liteTvNextName';
        // Until the EPG answers, the channel is the most that can honestly be said.
        name.textContent = tuned.currentName || (tuned.channelName || 'TV-Sender');
        text.appendChild(name);

        var meta = document.createElement('div');
        meta.className = 'liteTvPauseMeta';
        text.appendChild(meta);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvNextButtons';
        text.appendChild(buttons);

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

        buttons.appendChild(makeButton('⏭ Nächste Sendung', 'liteTvPill', skipToNext));

        buttons.appendChild(makeButton('↺ Von Anfang an', 'liteTvPill', function () {
            if (!tuned) {
                return;
            }
            removePausePanel();
            seekToStart();
        }));

        buttons.appendChild(makeButton('Sender verlassen', 'liteTvPill', function () {
            removePausePanel();
            leaveChannel();
        }));

        refresh();
        host.appendChild(panel);

        apiGet('LiteTv/Channels/' + tuned.channelId + '/Now?upcoming=8').then(function (now) {
            if (!tuned || !document.getElementById(PAUSE_PANEL_ID)) {
                return;
            }
            meta.innerHTML = '';
            function line(prefix, text) {
                var el = document.createElement('div');
                var time = document.createElement('span');
                time.className = 'liteTvEpgTime';
                time.textContent = prefix;
                el.appendChild(time);
                el.appendChild(document.createTextNode(text));
                meta.appendChild(el);
            }
            var current = now.Current && now.Current.ItemId === tuned.currentItemId ? now.Current : null;
            if (current) {
                var title = (current.SeriesName ? current.SeriesName + ': ' : '') + current.Name;
                name.textContent = title;
                tuned.currentName = title;
                art.innerHTML = '';
                addImage(art, current, 'poster', 260, 'liteTvArt');
                line('Jetzt', formatTime(current.StartUtc) + '–' + formatTime(current.EndUtc));
            }
            if (now.BlockName) {
                line('Sendung', now.BlockName);
            }
            // What comes next is read off the queue rather than off the clock: once the
            // viewer has skipped, the schedule and the player no longer agree, and the
            // player is the one that decides what they are about to watch.
            prepareNext().then(function (info) {
                var next = info && (tuned.mode === 'binge' && info.binge ? info.binge : info.schedule);
                if (next && document.getElementById(PAUSE_PANEL_ID)) {
                    line('Danach', (next.SeriesName ? next.SeriesName + ': ' : '') + next.Name);
                }
            });
        }).catch(function () { });
    }

    // ------------------------------------------------------- end-of-item logic

    function stopWatcher() {
        if (watchTimer) {
            clearInterval(watchTimer);
            watchTimer = null;
        }
    }

    function clearNextOverlay() {
        nextOverlayCountdown = null;
        nextOverlayCommit = null;
        removeOverlay(NEXT_OVERLAY_ID);
    }

    function commitNextOverlay() {
        if (nextOverlayCommit) {
            nextOverlayCommit();
            nextOverlayCommit = null;
        }
    }

    // How long before the end the binge choice is acted on. The card is up for far longer,
    // but the follow-up has to be queued while the current programme is still the one
    // playing - queue it after the player has already moved on and it lands a programme too
    // late. Once it is committed the choice buttons go, because there is no taking it back.
    var BINGE_COMMIT_SECONDS = 6;

    // How often the server is asked what the player is actually on. The player moves through
    // the queue by itself, so this is the only thing that says which programme is running -
    // and it is what makes an episode already watched by bingeing get stepped over when the
    // schedule comes round to it.
    var SESSION_POLL_MS = 3000;
    var lastSessionPoll = 0;

    function pollCurrentItem() {
        var now = Date.now();
        if (now - lastSessionPoll < SESSION_POLL_MS) {
            return;
        }
        lastSessionPoll = now;
        getOwnSession().then(function (session) {
            var playing = session && session.NowPlayingItem;
            if (!tuned || !playing || playing.Id === tuned.currentItemId) {
                return;
            }

            var isTheBingedOne = playing.Id === tuned.bingePlayingId;
            if (!isTheBingedOne && tuned.bingedIds.indexOf(playing.Id) >= 0) {
                // The schedule has reached an episode the viewer already watched by
                // carrying on with the series. Airing it again would be showing the same
                // episode twice for no reason other than that the schedule was written
                // before they chose otherwise.
                console.debug('liteTv: stepping over', playing.Name, '- already watched by bingeing');
                nextTrack();
                return;
            }
            if (!isTheBingedOne) {
                tuned.bingePlayingId = null;
            }

            tuned.currentItemId = playing.Id;
            tuned.currentSeriesId = playing.SeriesId || null;
            tuned.currentName = (playing.SeriesName ? playing.SeriesName + ': ' : '') + playing.Name;
            // Follow the queue rather than searching it: a channel loops, so the same
            // handful of titles come round repeatedly and the ids repeat with them.
            for (var i = tuned.index; i < tuned.queue.length; i++) {
                if (tuned.queue[i] === playing.Id) {
                    tuned.index = i;
                    break;
                }
            }
            // Every programme asks again. Following the schedule is what a channel is, so
            // that is what it always comes back to.
            tuned.mode = 'schedule';
        }).catch(function () { });
    }

    function startWatcher() {
        stopWatcher();
        // The watcher no longer drives playback - the player owns the queue. What is left is
        // watching: the pause panel, the card for what is coming, and the one moment where a
        // choice has to be acted on.
        var watchedItemId = null;
        var overlayShown = false;
        var committed = false;
        var nextInfo = null; // { schedule: ProgramDto|null, binge: ProgramDto|null }

        watchTimer = setInterval(function () {
            if (!tuned) {
                stopWatcher();
                return;
            }
            pollCurrentItem();

            if (tuned.currentItemId !== watchedItemId) {
                watchedItemId = tuned.currentItemId;
                overlayShown = false;
                committed = false;
                nextInfo = null;
                clearNextOverlay();
            }

            var video = document.querySelector('#videoOsdPage video') || document.querySelector('video');
            if (!video || !video.duration || isNaN(video.duration)) {
                return;
            }

            var paused = video.paused && !video.ended;
            if (paused) {
                ensurePausePanel();
            } else {
                removePausePanel();
            }

            var remaining = (video.duration - END_MARGIN_SECONDS) - video.currentTime;

            // Seeking back out of the window re-arms the card for the next approach.
            if (overlayShown && remaining > NEXT_OVERLAY_WINDOW_SECONDS + 10) {
                overlayShown = false;
                committed = false;
                clearNextOverlay();
            }

            if (remaining <= NEXT_OVERLAY_WINDOW_SECONDS && !overlayShown && !video.ended) {
                overlayShown = true;
                prepareNext().then(function (info) {
                    nextInfo = info;
                    if (tuned && info) {
                        showNextOverlay(info, remaining);
                    }
                });
            }

            // The countdown is driven from the player, not from a wall-clock timer, so it
            // holds while paused and follows along when the viewer seeks.
            if (nextOverlayCountdown) {
                nextOverlayCountdown(remaining, paused);
            }

            // Never while paused: the programme is not over for the viewer until they let it
            // play on. With nothing chosen there is nothing to do here at all - the queue
            // rolls on by itself, which is the whole point of playing one.
            if (remaining <= BINGE_COMMIT_SECONDS && !committed && !paused) {
                committed = true;
                if (tuned.mode === 'binge' && nextInfo && nextInfo.binge) {
                    queueBinged(nextInfo.binge);
                }
                commitNextOverlay();
            }
        }, 500);
    }

    // What the queue plays next, and what carrying on with the series would play instead.
    //
    // Both come out of the queue itself rather than from a fresh request. The queue IS the
    // schedule, so the next entry needs no asking; and the next episode of the series is
    // whatever the channel airs of it next, which is not the same as what /Shows/{id}/Episodes
    // would answer - that orders specials differently, and offering an episode the channel is
    // not going to play is how the choice ends up naming the wrong one.
    function prepareNext() {
        if (!tuned) {
            return Promise.resolve(null);
        }

        var schedule = null;
        var binge = null;
        var seriesId = tuned.currentSeriesId;
        for (var i = tuned.index + 1; i < tuned.airings.length; i++) {
            var airing = tuned.airings[i];
            if (tuned.bingedIds.indexOf(airing.ItemId) >= 0) {
                continue; // already watched by bingeing; the queue will step over it
            }
            if (!schedule) {
                schedule = airing;
            }
            if (!binge && seriesId && airing.SeriesId === seriesId) {
                binge = airing;
            }
            if (schedule && (binge || !seriesId)) {
                break;
            }
        }

        // Where the schedule's own next programme is the next episode anyway, there is no
        // choice to offer - only one thing is going to happen either way.
        if (binge && schedule && binge.ItemId === schedule.ItemId) {
            binge = null;
        }

        return Promise.resolve({ schedule: schedule, binge: binge });
    }

    // Carrying on with the series: the episode is put into the queue right after the one
    // playing, so the player moves to it by itself and then carries on with the schedule.
    // Whether it leaves a trace is the plugin's decision, not this script's.
    function queueBinged(episode) {
        if (!tuned) {
            return;
        }
        var sessionId = tuned.sessionId;
        var channelId = tuned.channelId;
        tuned.bingedIds.push(episode.ItemId);
        tuned.bingePlayingId = episode.ItemId;

        getFlags().then(function (flags) {
            return flags.shieldBingedEpisodes
                ? apiPost('LiteTv/Tuned?sessionId=' + encodeURIComponent(sessionId)
                    + '&channelId=' + channelId + '&itemId=' + episode.ItemId)
                : Promise.resolve();
        }).then(function () {
            return sendPlay(sessionId, [episode.ItemId], 0, 'PlayNext');
        }).catch(function (err) {
            console.warn('liteTv: could not queue the next episode', err);
        });
    }

    function showNextOverlay(info, countdownSeconds) {
        ensureStyle();
        removeOverlay(NEXT_OVERLAY_ID);

        var overlay = document.createElement('div');
        overlay.id = NEXT_OVERLAY_ID;

        var card = document.createElement('div');
        card.className = 'liteTvNextCard liteTvWithArt';
        overlay.appendChild(card);

        // The poster of whatever is actually going to run, so the card is recognisable
        // before it is read. It follows the schedule/binge choice below.
        var art = document.createElement('div');
        art.className = 'liteTvArtSlot';
        card.appendChild(art);

        var text = document.createElement('div');
        text.className = 'liteTvCardText';
        card.appendChild(text);

        var eyebrow = document.createElement('div');
        eyebrow.className = 'liteTvEyebrow';
        eyebrow.textContent = 'Als Nächstes';
        text.appendChild(eyebrow);

        var name = document.createElement('div');
        name.className = 'liteTvNextName';
        text.appendChild(name);

        var countdownText = document.createElement('div');
        countdownText.className = 'liteTvCountdownText';
        text.appendChild(countdownText);

        var buttons = document.createElement('div');
        buttons.className = 'liteTvNextButtons';
        text.appendChild(buttons);

        var bar = document.createElement('div');
        bar.className = 'liteTvCountdownBar';
        var barFill = document.createElement('div');
        barFill.style.width = '100%';
        bar.appendChild(barFill);
        text.appendChild(bar);

        var totalSeconds = Math.max(1, Math.ceil(countdownSeconds));
        var secondsLeft = totalSeconds;
        var isPaused = false;
        var scheduleBtn = null;
        var bingeBtn = null;
        // Without a series to carry on with there is no real choice - prepareNext already
        // drops the binge candidate where it is the same thing the schedule would play.
        // Then the card just says what is coming, with no buttons to press.
        var hasChoice = !!(info.binge && tuned && tuned.currentSeriesId);

        function programName(program) {
            if (!program) {
                return 'Programm';
            }
            return program.SeriesName ? program.SeriesName + ': ' + program.Name : program.Name;
        }

        function update() {
            var binging = tuned && tuned.mode === 'binge' && info.binge;
            var showing = binging ? info.binge : info.schedule;
            name.textContent = programName(showing);
            art.innerHTML = '';
            addImage(art, showing, 'poster', 220, 'liteTvArt');
            countdownText.textContent = isPaused
                ? 'startet, sobald du fortsetzt'
                : (secondsLeft > 0 ? 'startet in ' + secondsLeft + ' Sekunden' : 'startet gleich');
            barFill.style.width = Math.max(0, (secondsLeft / totalSeconds) * 100).toFixed(1) + '%';
            if (scheduleBtn) {
                scheduleBtn.classList.toggle('liteTvPillActive', !binging);
            }
            if (bingeBtn) {
                bingeBtn.classList.toggle('liteTvPillActive', !!binging);
            }
        }

        // Once the choice has been acted on the buttons go: the follow-up is in the player's
        // queue by then, so a button that looked like it still decided something would be
        // lying about what pressing it does.
        nextOverlayCommit = function () {
            if (bingeBtn && bingeBtn.parentNode) {
                bingeBtn.parentNode.removeChild(bingeBtn);
            }
            if (scheduleBtn && scheduleBtn.parentNode) {
                scheduleBtn.parentNode.removeChild(scheduleBtn);
            }
            bingeBtn = null;
            scheduleBtn = null;
        };

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
        }

        buttons.appendChild(makeButton('⏭ Jetzt starten', 'liteTvPill', skipToNext));

        update();

        // No timer of its own: the watcher feeds the real remaining time in, so the
        // countdown holds while paused and jumps along when the viewer seeks.
        nextOverlayCountdown = function (secondsRemaining, paused) {
            if (!document.getElementById(NEXT_OVERLAY_ID)) {
                nextOverlayCountdown = null;
                return;
            }
            secondsLeft = Math.max(0, Math.ceil(secondsRemaining));
            isPaused = !!paused;
            update();
        };

        getOsdContainer().appendChild(overlay);
        void overlay.offsetWidth;
        overlay.classList.add('liteTvVisible');
    }

    // Manual "next programme", from the pause panel or the card. The player owns the queue,
    // so this is its own next-track button: whatever it would have played next, now.
    // A binge choice made first is honoured, because it was queued as the next track.
    function skipToNext() {
        if (!tuned) {
            return;
        }
        clearNextOverlay();
        removePausePanel();
        if (tuned.mode === 'binge') {
            prepareNext().then(function (info) {
                if (info && info.binge) {
                    queueBinged(info.binge);
                }
                nextTrack();
            });
            return;
        }
        nextTrack();
    }

    // ------------------------------------------------------------------ guide

    function buildChannelCard(channel) {
        var card = document.createElement('div');
        card.className = 'liteTvCard';

        // Between programmes the card shows what is coming, so that is what it wears.
        var imageUrl = programImage(channel.Now || channel.Next, 'wide', 640);
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
        } else if (channel.Kind === 'Interstitial') {
            // Between programmes is not off air, and saying so is the difference between a
            // channel that looks broken and one that looks like a channel.
            now.textContent = channel.Next
                ? 'Werbung – gleich: ' + (channel.Next.SeriesName ? channel.Next.SeriesName + ': ' : '') + channel.Next.Name
                : 'Werbepause';
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

    // ------------------------------------------ hiding Jellyfin's own Live TV rows

    // With the channel row on the home screen, the server's own "Live TV" and "On Now" rows
    // are the same channels a second time, listed the way Live TV lists them. Hiding them is
    // a setting rather than a given, because they are only redundant once the row is there.
    //
    // A row is recognised two ways, because neither is enough on its own: by what it holds
    // (Live TV rows are the only home rows built from programmes and channels) and by its
    // heading, which catches a row whose cards have not rendered yet. The heading is
    // translated, so the list is the languages this plugin speaks; the structural test is
    // what covers the rest.
    var NATIVE_LIVE_TV_HEADINGS = [
        'live tv', 'live-tv', 'livetv',
        'on now', 'gerade läuft', 'läuft gerade', 'jetzt im tv'
    ];

    function isNativeLiveTvSection(section) {
        if (section.id === HOME_ROW_ID || section.classList.contains('liteTvHiddenSection')) {
            return false;
        }
        // Programmes and channels are cards no other home row is built from. Recordings are
        // left alone deliberately: they are things the viewer made, not the two rows this
        // setting is about.
        if (section.querySelector('[data-type="Program"], [data-type="TvChannel"]')) {
            return true;
        }
        var heading = section.querySelector('.sectionTitle');
        var text = heading ? (heading.textContent || '').trim().toLowerCase() : '';
        return !!text && NATIVE_LIVE_TV_HEADINGS.indexOf(text) >= 0;
    }

    function hideNativeLiveTvSections(page) {
        var host = page.querySelector('.homeSectionsContainer') || page;
        var sections = host.querySelectorAll('.verticalSection');
        for (var i = 0; i < sections.length; i++) {
            if (isNativeLiveTvSection(sections[i])) {
                // A class, not an inline style: setting the same style over and over would
                // keep waking the observer that called us.
                sections[i].classList.add('liteTvHiddenSection');
            }
        }
    }

    var nativeSectionObserver = null;

    function watchNativeLiveTvSections(page) {
        getFlags().then(function (flags) {
            if (!flags.enabled || !flags.hideNativeLiveTv) {
                return;
            }
            ensureStyle();
            hideNativeLiveTvSections(page);
            // The stock rows render after us and re-render on their own, so one sweep at
            // page load would only catch whichever of them happened to be there already.
            // Marking a row adds a class and nothing more, so the sweep this wakes finds
            // nothing left to do and settles.
            if (nativeSectionObserver) {
                nativeSectionObserver.disconnect();
            }
            nativeSectionObserver = new MutationObserver(function () {
                hideNativeLiveTvSections(page);
            });
            nativeSectionObserver.observe(page, { childList: true, subtree: true });
        });
    }

    function closeGuide() {
        removeOverlay(GUIDE_ID);
    }

    // The guide is a time grid: one row per channel, time running left to right, every
    // programme drawn as wide as it is long. That is what makes a schedule readable at a
    // glance - which channel is between programmes, what overlaps what, how long there is
    // left of the thing on now. A list of "now" and "next" per channel cannot show any of it.
    var GUIDE_HOURS = 4;
    var GUIDE_HOUR_WIDTH = 15; // em per hour
    var guideStartMs = null;

    function guideWindowStart() {
        if (guideStartMs !== null) {
            return guideStartMs;
        }
        // Start the window on the half hour before now, so the grid lines land on the
        // times a viewer thinks in rather than on whatever minute it happens to be.
        var now = new Date();
        now.setSeconds(0, 0);
        now.setMinutes(now.getMinutes() < 30 ? 0 : 30);
        return now.getTime();
    }

    function deviceButton(channel, devices) {
        return makeButton('Auf Gerät…', 'liteTvPill', function () {
            if (devices.style.display === 'block') {
                devices.style.display = 'none';
                return;
            }
            devices.style.display = 'block';
            devices.innerHTML = '';
            window.ApiClient.getSessions().then(function (sessions) {
                var ownDeviceId = window.ApiClient.deviceId();
                var targets = (sessions || []).filter(function (s) {
                    return s.DeviceId !== ownDeviceId && s.SupportsRemoteControl !== false;
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
        });
    }

    function buildProgramBlock(program, startMs, spanMs, nowMs) {
        var from = new Date(program.StartUtc).getTime();
        var to = new Date(program.EndUtc).getTime();
        var left = ((from - startMs) / spanMs) * 100;
        var width = ((to - from) / spanMs) * 100;

        // Clip to the window: the first and last programmes of a row usually hang over it.
        if (left < 0) {
            width += left;
            left = 0;
        }
        width = Math.min(width, 100 - left);
        if (width <= 0) {
            return null;
        }

        var el = document.createElement('div');
        el.className = 'liteTvProg'
            + (program.Kind === 'Interstitial' ? ' liteTvProgFill' : '')
            + (program.Kind === 'OffAir' ? ' liteTvProgDark' : '')
            + (from <= nowMs && nowMs < to && program.Kind === 'Program' ? ' liteTvProgOn' : '');
        el.style.left = left + '%';
        el.style.width = width + '%';

        // A poster in the block is what makes a row of the grid readable as programmes
        // rather than as a row of labels. Only where the block is wide enough to hold one:
        // below that it would crowd out the title, which matters more.
        if (width * (spanMs / 3600000) * GUIDE_HOUR_WIDTH / 100 >= 6) {
            addImage(el, program, 'poster', 120, 'liteTvProgArt');
        }

        var text = document.createElement('div');
        text.className = 'liteTvProgText';
        el.appendChild(text);

        var title = document.createElement('div');
        title.className = 'liteTvProgTitle';
        title.textContent = program.Kind === 'Interstitial' && program.NextProgramName
            ? 'Werbung – gleich: ' + program.NextProgramName
            : (program.SeriesName ? program.SeriesName + ': ' : '') + program.Name;
        text.appendChild(title);

        var time = document.createElement('div');
        time.className = 'liteTvProgTime';
        time.textContent = formatTime(program.StartUtc) + '–' + formatTime(program.EndUtc)
            + (program.BlockName ? ' · ' + program.BlockName : '');
        text.appendChild(time);

        el.title = title.textContent + '\n' + time.textContent;
        return el;
    }

    function renderGuideGrid(host, guide) {
        host.innerHTML = '';
        var startMs = new Date(guide.StartUtc).getTime();
        var endMs = new Date(guide.EndUtc).getTime();
        var spanMs = endMs - startMs;
        var nowMs = new Date(guide.ServerTimeUtc).getTime();
        var trackWidth = ((spanMs / 3600000) * GUIDE_HOUR_WIDTH) + 'em';

        function track() {
            var el = document.createElement('div');
            el.className = 'liteTvTrack';
            el.style.flex = '0 0 ' + trackWidth;
            el.style.width = trackWidth;
            return el;
        }

        function nowLine() {
            if (nowMs < startMs || nowMs > endMs) {
                return null;
            }
            var line = document.createElement('div');
            line.className = 'liteTvNowLine';
            line.style.left = (((nowMs - startMs) / spanMs) * 100) + '%';
            return line;
        }

        var axis = document.createElement('div');
        axis.className = 'liteTvAxis';
        var spacer = document.createElement('div');
        spacer.className = 'liteTvAxisSpacer';
        axis.appendChild(spacer);
        var axisTrack = track();
        for (var tick = startMs; tick < endMs; tick += 1800000) {
            var label = document.createElement('div');
            label.className = 'liteTvTick';
            label.style.left = (((tick - startMs) / spanMs) * 100) + '%';
            label.textContent = formatTime(new Date(tick).toISOString());
            axisTrack.appendChild(label);
        }
        axis.appendChild(axisTrack);
        host.appendChild(axis);

        guide.Channels.forEach(function (channel) {
            var row = document.createElement('div');
            row.className = 'liteTvRow';

            var nameCell = document.createElement('div');
            nameCell.className = 'liteTvRowName';
            var name = document.createElement('div');
            name.className = 'liteTvGuideName';
            name.textContent = channel.Name;
            name.addEventListener('click', function () { tuneIn(channel.Id); });
            nameCell.appendChild(name);

            var devices = document.createElement('div');
            devices.className = 'liteTvDevices';
            nameCell.appendChild(deviceButton(channel, devices));
            row.appendChild(nameCell);

            var rowTrack = track();
            channel.Programs.forEach(function (program) {
                var block = buildProgramBlock(program, startMs, spanMs, nowMs);
                if (!block) {
                    return;
                }
                if (block.className.indexOf('liteTvProgOn') >= 0) {
                    block.addEventListener('click', function () { tuneIn(channel.Id); });
                }
                rowTrack.appendChild(block);
            });
            var line = nowLine();
            if (line) {
                rowTrack.appendChild(line);
            }
            row.appendChild(rowTrack);
            host.appendChild(row);
            host.appendChild(devices);
        });
    }

    function loadGuide(host) {
        var start = guideWindowStart();
        return apiGet('LiteTv/Guide?hours=' + GUIDE_HOURS + '&from=' + encodeURIComponent(new Date(start).toISOString()))
            .then(function (guide) {
                if (!guide.Channels.length) {
                    host.innerHTML = '';
                    var empty = document.createElement('div');
                    empty.textContent = 'Keine Sender konfiguriert. Sender werden im Dashboard unter Plugins → LiteTV Channels angelegt.';
                    host.appendChild(empty);
                    return;
                }
                renderGuideGrid(host, guide);
            });
    }

    function openGuide() {
        closeGuide();
        ensureStyle();
        guideStartMs = null;

        var backdrop = document.createElement('div');
        backdrop.id = GUIDE_ID;
        backdrop.addEventListener('click', function (e) {
            if (e.target === backdrop) {
                closeGuide();
            }
        });

        var panel = document.createElement('div');
        panel.className = 'liteTvPanel liteTvPanelWide';
        backdrop.appendChild(panel);

        var bar = document.createElement('div');
        bar.className = 'liteTvGuideBar';
        var heading = document.createElement('h2');
        heading.textContent = '📺 TV-Sender';
        bar.appendChild(heading);
        panel.appendChild(bar);

        var grid = document.createElement('div');
        grid.className = 'liteTvGrid';
        panel.appendChild(grid);

        function jumpTo(edge) {
            grid.style.scrollBehavior = 'auto';
            grid.scrollLeft = edge === 'end' ? grid.scrollWidth : 0;
            grid.style.scrollBehavior = '';
        }

        // Moving through the guide is one gesture, not two: the arrows slide the grid a
        // screenful at a time, and when there is no more of the loaded window in that
        // direction they roll on to the next one and land at its near edge. So the schedule
        // reads as one continuous strip rather than as a series of separate loads, and the
        // same arrows keep working at the ends instead of quietly doing nothing.
        function shift(hours, edge) {
            guideStartMs = (guideStartMs === null ? guideWindowStart() : guideStartMs) + (hours * 3600000);
            return loadGuide(grid).then(function () {
                jumpTo(edge);
            }).catch(function (err) { console.debug('liteTv: guide not available', err); });
        }

        function page(direction) {
            var furthest = grid.scrollWidth - grid.clientWidth;
            var step = Math.max(120, grid.clientWidth * 0.8);
            // A couple of pixels of slack: sub-pixel layout means scrollLeft rarely lands
            // exactly on the end, and an arrow that stops working there looks broken.
            if (direction > 0 && grid.scrollLeft >= furthest - 2) {
                return shift(GUIDE_HOURS, 'start');
            }
            if (direction < 0 && grid.scrollLeft <= 2) {
                return shift(-GUIDE_HOURS, 'end');
            }
            grid.scrollTo({
                left: Math.max(0, Math.min(furthest, grid.scrollLeft + (direction * step))),
                behavior: 'smooth'
            });
            return Promise.resolve();
        }

        bar.appendChild(makeButton('◀', 'liteTvPill', function () { page(-1); }));
        bar.appendChild(makeButton('Jetzt', 'liteTvPill', function () {
            guideStartMs = null;
            loadGuide(grid).then(function () { jumpTo('start'); })
                .catch(function (err) { console.debug('liteTv: guide not available', err); });
        }));
        bar.appendChild(makeButton('▶', 'liteTvPill', function () { page(1); }));

        document.body.appendChild(backdrop);
        void backdrop.offsetWidth;
        backdrop.classList.add('liteTvVisible');

        loadGuide(grid).catch(function (err) {
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
                startWatcher();
                return;
            }
            // Not our playback as far as this tab knows - but it may be a channel switched
            // on from the library entry, which the web client takes over.
            getFlags().then(function (flags) {
                if (flags.enabled) {
                    adoptChannelPlayback();
                }
            });
            return;
        }

        if (isHome(e) && e.target) {
            renderHomeRow(e.target);
            watchNativeLiveTvSections(e.target);
        }

        // Landing anywhere that is not the video OSD means the viewer has left the
        // channel. No mid-chain exception is needed any more: the player holds the whole
        // queue, so it never leaves the OSD between programmes the way re-issuing playback
        // for each one did.
        if (tuned) {
            untune();
        }
    });

    document.addEventListener('viewhide', function (e) {
        if (e && e.target && e.target.id === 'videoOsdPage') {
            stopWatcher();
            removeOverlay(TUNE_OVERLAY_ID);
            clearNextOverlay();
            removePausePanel();
        }
    });
})();
