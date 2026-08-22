/*
 * Enough of Jellyfin's dashboard to draw the configuration page outside it: the globals the
 * page reaches for, and fixture answers shaped like the real endpoints. It is here to look at
 * layout - the rail at a lineup's worth of channels, whether a screen fits its window - not to
 * stand in for a server.
 */
(function () {
    var CHANNELS = [
        { name: 'Action-Kanal', kind: 'Program', now: 'Avatar' },
        { name: 'Sci-Fi Nacht', kind: 'Trailer', now: 'Dune' },
        { name: 'Kinderprogramm', kind: 'OffAir', now: null },
        { name: 'Marathon: Alien', kind: 'Program', now: 'Aliens' },
        { name: 'Heimatfilme', kind: 'OffAir', now: null },
        { name: 'Tatort-Kanal', kind: 'Program', now: 'Tatort: Borowski' },
        { name: 'Western', kind: 'Program', now: 'Spiel mir das Lied vom Tod' },
        { name: 'Komödie', kind: 'Advert', now: null },
        { name: '80er Nacht', kind: 'Program', now: 'Die Hard' },
        { name: 'Dokus', kind: 'Program', now: 'Planet Erde' },
        { name: 'Serien am Nachmittag', kind: 'OffAir', now: null },
        { name: 'Weihnachten', kind: 'OffAir', now: null },
        { name: 'Horror nach Mitternacht', kind: 'OffAir', now: null },
        { name: 'Ridley Scott', kind: 'Program', now: 'Gladiator' },
        { name: 'Kurzfilme', kind: 'Program', now: 'La Jetée' },
        { name: 'Musikvideos', kind: 'OffAir', now: null }
    ];

    function guid(i) {
        return 'aaaaaaaa-bbbb-4ccc-8ddd-' + ('00000000000' + i).slice(-12);
    }

    var config = {
        ChannelUserName: 'LiteTV',
        SkipTrailerSegments: true,
        YouTubeClient: '',
        Channels: CHANNELS.map(function (c, i) {
            return {
                Id: guid(i),
                Name: c.name,
                Enabled: c.kind !== 'OffAir' || i % 3 !== 2,
                AnchorUtc: '2026-08-17T00:00:00Z',
                Sources: i === 0 ? [
                    { ItemId: guid(90), Name: 'Avatar', Type: 'Movie' },
                    { ItemId: guid(91), Name: 'Die Hard', Type: 'Movie' },
                    { ItemId: guid(92), Name: 'Alien-Reihe', Type: 'BoxSet' }
                ] : [],
                Blocks: [],
                Order: 'Sequential',
                SlotMinutes: 30,
                EpisodesPerBlock: 0,
                TrailersInGaps: true,
                Trailers: 'Upcoming',
                TrailerEveryPrograms: 3,
                TrailerLookahead: 3,
                TrailerTitles: [],
                TrailerSlots: [],
                Adverts: i === 0 ? [
                    { Url: 'https://youtu.be/aaa', Name: 'Persil 1994', Decade: 1990 },
                    { Url: 'https://youtu.be/bbb', Name: 'Opel Corsa', Decade: 1990 }
                ] : [],
                Artwork: {}
            };
        })
    };

    // A week with a plausible evening on it, so the grid has something to draw.
    var airings = [];
    var id = 0;
    for (var day = 0; day < 7; day++) {
        var t = day * 86400 + 18 * 3600;
        for (var n = 0; n < 5; n++) {
            airings.push({
                Id: 'air-' + (id++),
                StartSecond: t,
                DurationSeconds: 5400,
                Kind: 'Program',
                Name: ['Avatar', 'Die Hard', 'Aliens', 'Gladiator', 'Heat'][n],
                ItemId: guid(90)
            });
            t += 5400;
            airings.push({
                Id: 'air-' + (id++),
                StartSecond: t,
                DurationSeconds: 600,
                Kind: n % 2 ? 'Trailer' : 'Advert',
                Name: n % 2 ? 'Vorschau: Dune' : 'Persil 1994',
                Url: 'https://youtu.be/aaa'
            });
            t += 600;
        }
    }

    var week = { Curated: true, ModifiedUtc: new Date().toISOString(), Airings: airings };

    function now(count) {
        var out = { Now: null, Upcoming: [] };
        var base = Date.now();
        for (var i = 0; i < count; i++) {
            var row = {
                StartUtc: new Date(base + i * 5400000).toISOString(),
                Kind: i % 3 === 1 ? 'Trailer' : 'Program',
                Name: ['Avatar', 'Vorschau: Dune', 'Die Hard', 'Aliens'][i % 4]
            };
            if (i === 0) { out.Now = row; } else { out.Upcoming.push(row); }
        }
        return out;
    }

    window.ApiClient = {
        serverAddress: function () { return ''; },
        getUrl: function (path, params) {
            var q = params ? Object.keys(params).map(function (k) {
                return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]);
            }).join('&') : '';
            return path + (q ? '?' + q : '');
        },
        getPluginConfiguration: function () { return Promise.resolve(config); },
        updatePluginConfiguration: function () { window.__ltvSaved = true; return Promise.resolve({}); },
        getJSON: function (url) {
            if (url.indexOf('LiteTv/Channels?') === 0 || url === 'LiteTv/Channels') {
                return Promise.resolve({
                    Channels: config.Channels.map(function (c, i) {
                        var f = CHANNELS[i];
                        return {
                            Id: c.Id,
                            Name: c.Name,
                            Kind: f.kind,
                            // Two of the sixteen have never been laid out, which is what the
                            // rail's "no week" tag is for.
                            Curated: !(i === 4 || i === 15),
                            Now: f.now ? { Name: f.now, StartUtc: new Date(Date.now() - 900000).toISOString() } : null,
                            Next: { Name: 'Die Hard', StartUtc: new Date(Date.now() + 2700000).toISOString() },
                            Image: {}
                        };
                    })
                });
            }
            if (url.indexOf('/Now') > 0) { return Promise.resolve(now(12)); }
            if (url.indexOf('/Week') > 0) { return Promise.resolve(week); }
            if (url.indexOf('LiteTv/Plugins') === 0) {
                return Promise.resolve([
                    { Id: '1', Name: 'Smart Similar', Installed: true, Usable: true, Version: '1.2.0.0', Status: 'Active', WhyItMatters: '' },
                    { Id: '2', Name: 'Collection Row', Installed: true, Usable: true, Version: '1.3.0.0', Status: 'Active', WhyItMatters: '' },
                    { Id: '3', Name: 'FSK Rating Updater', Installed: true, Usable: false, Version: '1.2.9.0', Status: 'Disabled', WhyItMatters: '' },
                    { Id: '4', Name: 'SponsorBlock Segments', Installed: false, Usable: false, Version: null, Status: null, WhyItMatters: 'Trims sponsor and outro stretches off linked trailers.' }
                ]);
            }
            if (url.indexOf('LiteTv/Suggestions') === 0) { return Promise.resolve([]); }
            if (url.indexOf('LiteTv/Update/Builds') === 0) { return Promise.resolve([]); }
            if (url.indexOf('Items') === 0) { return Promise.resolve({ Items: [] }); }
            return Promise.resolve({});
        },
        // The page asks who is looking before it fetches anything per-user. Missing here, the
        // exception took out whatever click was in flight - which is how a stub gap came to
        // look like the channel rail being broken.
        getCurrentUserId: function () { return 'ffffffffffffffffffffffffffffffff'; },
        getCurrentUser: function () { return Promise.resolve({ Id: 'ffffffffffffffffffffffffffffffff' }); },

        /** Members of a series or a collection, for the dealt-queue preview on Content. */
        getItems: function (userId, query) {
            query = query || {};
            if (query.IncludeItemTypes === 'Episode') {
                return Promise.resolve({
                    Items: [1, 2, 3, 4, 5, 6].map(function (n) {
                        return {
                            Id: 'ep' + n,
                            Name: 'Episode ' + n,
                            ParentIndexNumber: 1,
                            IndexNumber: n
                        };
                    })
                });
            }
            if (query.ParentId) {
                return Promise.resolve({
                    Items: [
                        { Id: 'm1', Name: 'Die Hard', ProductionYear: 1988 },
                        { Id: 'm2', Name: 'Aliens', ProductionYear: 1986 }
                    ]
                });
            }
            if (query.SearchTerm) {
                return Promise.resolve({
                    Items: [{ Id: 's1', Name: query.SearchTerm + ' (a result)', Type: 'Movie', ProductionYear: 1999 }]
                });
            }
            return Promise.resolve({ Items: [] });
        },

        ajax: function (o) { return window.ApiClient.getJSON(typeof o === 'string' ? o : o.url); },
        fetch: function (o) { return window.ApiClient.getJSON(typeof o === 'string' ? o : o.url); }
    };

    window.Dashboard = {
        showLoadingMsg: function () { },
        hideLoadingMsg: function () { },
        processPluginConfigurationUpdateResult: function () { },
        alert: function () { }
    };
})();
