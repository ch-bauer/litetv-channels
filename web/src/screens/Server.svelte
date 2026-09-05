<script lang="ts">
    /*
        Server settings — one screen with sub-tabs, as the board has it, rather than the two rail
        destinations the old page grew.

        "Playback & trailers" is the account channels play as, the trailer resolver and what the
        proof-of-origin token is doing. "App updates" is the store this server hands the
        television.
    */
    import Card from '../lib/ui/Card.svelte';
    import { store } from '../lib/config.svelte';
    import { api, authHeaders, dashboard, failureWords } from '../lib/jellyfin';

    interface PoToken {
        Held: boolean;
        TokenState: 'held' | 'expired' | 'missing';
        MintedUtc: string | null;
        AgeSeconds: number | null;
        HasPlayerToken: boolean;
        /** What the last resolution produced, or null if nothing has been resolved yet. */
        LastResolved: string | null;
        LastResolvedLow: boolean;
    }

    interface Build {
        FileName: string;
        /** "Wholphin" or "Findroid" - each family has its own idea of what "the latest" is. */
        Family: string;
        Version: string;
        BuildType: string;
        Abi: string | null;
        Bytes: number;
        /** When it was uploaded. */
        Modified: string;
        /** Whether this exact file is one of the assets its family's app is currently offered. */
        Offered: boolean;
    }

    interface BuildList {
        LatestVersion: string | null;
        UpdateUrl: string;
        FindroidLatestVersion: string | null;
        FindroidUpdateUrl: string;
        Builds: Build[];
    }

    interface ServerUser { Id: string; Name: string; }

    /**
     * One of the plugins LiteTV leans on, as the server reports it.
     *
     * The server answers this in one place so that the page and the television app ask the same
     * question rather than each interrogating Jellyfin and disagreeing.
     */
    interface SiblingPlugin {
        Id: string;
        Name: string;
        Installed: boolean;
        Version: string | null;
        Status: string | null;
        /** Installed AND able to answer. The two are not the same, and the difference shows. */
        Usable: boolean;
        WhyItMatters: string;
    }

    /** The drop-down's escape hatch. Not a name any account can have. */
    const NEW_ACCOUNT = '\u0000another';

    let pane = $state<'playback' | 'updates'>('playback');
    let users = $state<ServerUser[]>([]);
    let clients = $state<string[]>([]);
    let namingNewAccount = $state(false);
    let clearing = $state(false);
    let po = $state<PoToken | null>(null);
    let builds = $state<BuildList | null>(null);
    let buildsError = $state<string | null>(null);
    let uploading = $state(false);
    let uploadDone = $state(0);
    let uploadTotal = $state(0);
    let accountHelp = $state(false);
    let skipHelp = $state(false);
    let siblings = $state<SiblingPlugin[] | null>(null);

    const config = $derived(store.config);
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    /*
        Which of the sibling plugins are there.

        This strip existed on the old configuration page and was lost in the rebuild - the
        endpoint went on being served, and nothing asked it. It is worth having precisely
        because these plugins fail QUIETLY: a channel still airs without Smart Similar, the
        suggestions are simply blunter, and nothing anywhere says so. Same for the age badge
        and the collection row.
    */
    $effect(() => {
        api().getJSON<SiblingPlugin[]>(api().getUrl('LiteTv/Plugins'))
            .then((rows) => { siblings = rows; })
            // Not fatal, and not worth a red line: the page's own work does not depend on it.
            .catch(() => { siblings = []; });
    });

    $effect(() => {
        api().getJSON<PoToken>(api().getUrl('LiteTv/PoToken'))
            .then((answer) => (po = answer))
            .catch(() => (po = null));
    });

    /*
        Both of these are offered rather than typed. A typed client name that matches nothing in
        the ladder falls back to the whole ladder, and a typed account name that is not an
        account is made into one - neither of which is visible from the page, so both read as
        the setting being ignored. The client list is the resolver's OWN ladder, so the page and
        the resolver cannot drift apart.
    */
    $effect(() => {
        api().getJSON<{ Items?: ServerUser[] } | ServerUser[]>(api().getUrl('Users'))
            .then((answer) => {
                users = Array.isArray(answer) ? answer : answer.Items ?? [];
            })
            .catch(() => (users = []));
    });

    $effect(() => {
        api().getJSON<string[]>(api().getUrl('LiteTv/YouTubeClients'))
            .then((answer) => (clients = answer ?? []))
            .catch(() => (clients = []));
    });

    /** Whether the server already has the account the configuration names. */
    const accountKnown = $derived(
        config !== null && users.some((u) => u.Name === config.ChannelUserName));

    /*
        Every build the store holds except the ones actually on offer. The store holds two
        families (Wholphin and Findroid) with entirely separate version numbers, so "on offer"
        is answered per build by the server - see BuildDto.Offered / UpdateController.IsOffered
        - rather than compared against a single shared "latest version" here. Comparing against
        one shared version was the bug: every Findroid build's version never matched Wholphin's
        LatestVersion, so "remove what nobody is offered" deleted every Findroid build, including
        the one actually being served.
    */
    const supplanted = $derived.by(() => {
        const held = builds;
        if (!held) { return [] as Build[]; }
        return held.Builds.filter((b) => !b.Offered);
    });

    async function clearOutOldBuilds(): Promise<void> {
        const going = supplanted;
        if (going.length === 0) { return; }
        clearing = true;
        try {
            for (const build of going) {
                await api().fetch({
                    url: api().getUrl('LiteTv/Update/Builds/' + encodeURIComponent(build.FileName)),
                    type: 'DELETE',
                });
            }
        } catch (err) {
            dashboard().alert(failureWords(err));
        } finally {
            // Whatever happened, the list is re-read: half a clear-out must not be drawn as if
            // it had not happened at all.
            await loadBuilds();
            clearing = false;
        }
    }

    async function loadBuilds(): Promise<void> {
        buildsError = null;
        try {
            builds = await api().getJSON<BuildList>(api().getUrl('LiteTv/Update/Builds'));
        } catch (err) {
            buildsError = failureWords(err);
        }
    }

    $effect(() => {
        if (pane === 'updates' && builds === null && buildsError === null) {
            void loadBuilds();
        }
    });

    async function uploadOne(file: File): Promise<void> {
        const answer = await fetch(
            api().getUrl('LiteTv/Update/Builds/' + encodeURIComponent(file.name)),
            { method: 'POST', headers: authHeaders(), body: file },
        );
        if (!answer.ok) { throw new Error(answer.status + ' ' + answer.statusText); }
    }

    /*
        One file at a time on purpose - the ABI splits ship four APKs (arm64-v8a, armeabi-v7a,
        x86_64, x86) plus a Findroid build alongside them, and picking those five files one at a
        time was the actual complaint. Sent to the server in sequence rather than in parallel so
        a slow upload cannot race a fast one and land the wrong "most recently written" file as
        whichever a page reload happens to catch mid-flight.
    */
    async function upload(files: FileList | File[]): Promise<void> {
        const bar = dashboard();
        const list = Array.from(files);
        if (list.length === 0) { return; }

        uploading = true;
        uploadDone = 0;
        uploadTotal = list.length;
        const failed: string[] = [];

        try {
            for (const file of list) {
                try {
                    await uploadOne(file);
                } catch (err) {
                    failed.push(file.name + ': ' + failureWords(err));
                } finally {
                    uploadDone += 1;
                }
            }
        } finally {
            await loadBuilds();
            uploading = false;
        }

        if (failed.length > 0) {
            bar.alert(
                (failed.length === 1 ? 'A build' : failed.length + ' builds')
                    + ' could not be uploaded:\n' + failed.join('\n'));
        }
    }

    async function removeBuild(name: string): Promise<void> {
        try {
            await api().fetch({
                url: api().getUrl('LiteTv/Update/Builds/' + encodeURIComponent(name)),
                type: 'DELETE',
            });
            await loadBuilds();
        } catch (err) {
            dashboard().alert(failureWords(err));
        }
    }

    function megabytes(bytes: number): string {
        return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    }

    /*
        When a build was uploaded, said in full and in the reader's own time zone. The store shows
        several builds of the same version, and the only thing that tells them apart at a glance
        is when each arrived.
    */
    function uploadedAt(value: string): string {
        const at = new Date(value);
        if (Number.isNaN(at.getTime())) { return value; }
        return at.toLocaleString(undefined, {
            year: 'numeric', month: 'short', day: '2-digit',
            hour: '2-digit', minute: '2-digit',
        });
    }

    // Both places that say how old something is say it the same way. The token line used to
    // do its own arithmetic and reported a six-hour token as "minted 147 min ago", which reads
    // like a fault rather than a token halfway through an ordinary life.
    function minutesAgo(minutes: number): string {
        if (minutes < 1) { return 'just now'; }
        if (minutes < 60) { return minutes + ' min ago'; }
        const hours = Math.round(minutes / 60);
        if (hours < 24) { return hours + (hours === 1 ? ' hour ago' : ' hours ago'); }
        const days = Math.round(hours / 24);
        return days + (days === 1 ? ' day ago' : ' days ago');
    }

    function howLongAgo(value: string): string {
        const at = new Date(value).getTime();
        if (Number.isNaN(at)) { return ''; }
        return minutesAgo(Math.round((Date.now() - at) / 60000));
    }

    const poLine = $derived.by(() => {
        if (!po) { return 'not known'; }
        if (po.TokenState === 'expired') { return 'expired — trailers are capped at 360p'; }
        if (!po.Held || po.TokenState === 'missing') { return 'none held — trailers are capped at 360p'; }
        const age = po.AgeSeconds === null ? '' : ' · minted ' + minutesAgo(Math.round(po.AgeSeconds / 60));
        return 'held' + age + (po.HasPlayerToken ? ' · with a player token' : '');
    });
</script>

<div class="screen">
    <header>
        <h1>{german ? 'Servereinstellungen' : 'Server settings'}</h1>
        <div class="spacer"></div>
        {#if store.canRevert}
            <button type="button" class="save" onclick={() => void store.revert()}>{german ? 'Änderungen zurücksetzen' : 'Revert changes'}</button>
        {/if}
    </header>

    <nav class="subtabs">
        <button type="button" class:on={pane === 'playback'} onclick={() => (pane = 'playback')}>{german ? 'Wiedergabe & Trailer' : 'Playback & trailers'}</button>
        <button type="button" class:on={pane === 'updates'} onclick={() => (pane = 'updates')}>{german ? 'App-Updates' : 'App updates'}</button>
    </nav>

    {#if !config}
        <p class="none">Loading…</p>
    {:else if pane === 'playback'}
        <div class="body">
            <div class="left">
                <div class="field">
                    <div class="label-row">
                        <span class="label">{german ? 'Wiedergabekonto des Kanals' : 'Channel playback account'}</span>
                        <button type="button" class="help" class:on={accountHelp} onclick={() => (accountHelp = !accountHelp)} aria-label="About the playback account">?</button>
                    </div>
                    {#if users.length === 0}
                        <input class="text" bind:value={config.ChannelUserName} />
                    {:else if namingNewAccount || !accountKnown}
                        <input class="text" bind:value={config.ChannelUserName} />
                        <p class="note">
                            No account of that name yet - the server makes one the first time a
                            channel plays.
                            <button type="button" class="link" onclick={() => (namingNewAccount = false)}>
                                Pick an existing account instead
                            </button>
                        </p>
                    {:else}
                        <select
                            class="text"
                            value={config.ChannelUserName}
                            onchange={(e) => {
                                if (e.currentTarget.value === NEW_ACCOUNT) {
                                    namingNewAccount = true;
                                    return;
                                }
                                config.ChannelUserName = e.currentTarget.value;
                            }}
                        >
                            {#each users as user (user.Id)}
                                <option value={user.Name}>{user.Name}</option>
                            {/each}
                            <option value={NEW_ACCOUNT}>{german ? 'Anderen Namen…' : 'Another name...'}</option>
                        </select>
                    {/if}
                    <p class="note">{german ? 'Die Kanalwiedergabe wird diesem Konto zugeordnet, niemals deinem.' : 'Channel viewing is recorded against this account, never yours.'}</p>
                    {#if accountHelp}
                        <p class="deeper">A channel plays with this account's token, so what it watches lands on its watch history and not on the account of whoever is looking. That is the whole reason it exists.</p>
                    {/if}
                </div>

                <div class="field">
                    <div class="label-row">
                        <span class="label">{german ? 'Trailerteile überspringen, die nicht zum Trailer gehören' : 'Skip the parts of a trailer that are not the trailer'}</span>
                        <button type="button" class="help" class:on={skipHelp} onclick={() => (skipHelp = !skipHelp)} aria-label="About skipping">?</button>
                    </div>
                    <label class="check">
                        <input
                            type="checkbox"
                            checked={config.SkipTrailerSegments}
                            onchange={(e) => { config.SkipTrailerSegments = e.currentTarget.checked; }}
                        />
                        <span>{german ? 'SponsorBlock fragen und markierte Teile überspringen' : 'Ask SponsorBlock and skip what it names'}</span>
                    </label>
                    {#if skipHelp}
                        <p class="deeper">The uploader's branded card and the plea to subscribe are not the trailer. With this on, they are skipped and a break is sized by what actually plays.</p>
                    {/if}
                </div>

                <div class="field">
                    <span class="label">{german ? 'TMDb-API-Schlüssel (optional)' : 'TMDb API key (optional)'}</span>
                    <input
                        class="text"
                        type="password"
                        bind:value={config.TmdbApiKey}
                        placeholder={german ? 'ohne Schlüssel: kein Studio-Logo online' : 'without a key: no online studio logo'}
                    />
                    <p class="note">
                        {german
                            ? 'Nur für ein Studio-Logo bei Studio-Kanalvorschlägen, wenn die Bibliothek selbst keins hat. Ein kostenloser Schlüssel von themoviedb.org/settings/api genügt; ohne Schlüssel entfällt nur diese eine Grafik.'
                            : 'Used only to fetch a studio\'s own logo for a studio channel suggestion when the library has no picture for it. A free key from themoviedb.org/settings/api is enough; without one, only that one picture is skipped.'}
                    </p>
                </div>

                <div class="field">
                    <span class="label">{german ? 'Sprache der Konfigurationsseite' : 'Configuration page language'}</span>
                    <select class="text" bind:value={config.PageLanguage}>
                        <option value="auto">{german ? 'Automatisch' : 'Automatic'}</option>
                        <option value="en">English</option>
                        <option value="de">Deutsch</option>
                    </select>
                    <p class="note">{german ? 'Die LiteTV-Konfigurationsseite verwendet diese Sprache beim nächsten Aufruf.' : 'The LiteTV configuration page uses this language after the next visit.'}</p>
                </div>

                <div class="field">
                    <span class="label">{german ? 'YouTube-Sprache' : 'Ask YouTube in'}</span>
                    <!--
                        A free field, not a list of two. YouTube takes any language tag, and
                        offering "English or German" here would be an answer dressed up as a
                        question.
                    -->
                    <input
                        class="text"
                        bind:value={config.YouTubeLanguage}
                        placeholder={config.PageLanguage && config.PageLanguage !== 'auto'
                            ? config.PageLanguage
                            : 'follows this page'}
                    />
                    <p class="note">
                        What a YouTube programme is called in the schedule. A title has one per
                        language the uploader wrote one in; asking in a language that has none
                        gets the original back, so nothing is lost.
                    </p>
                </div>

                <div class="field">
                    <span class="label">{german ? 'YouTube-Client' : 'Ask YouTube as'}</span>
                    {#if clients.length === 0}
                        <input class="text" bind:value={config.YouTubeClient} placeholder="default" />
                    {:else}
                        <select class="text" bind:value={config.YouTubeClient}>
                            <option value="">{german ? 'Alle der Reihe nach versuchen' : 'Try them all, in order'}</option>
                            {#each clients as client (client)}
                                <option value={client}>{client}</option>
                            {/each}
                            {#if config.YouTubeClient && !clients.includes(config.YouTubeClient)}
                                <!--
                                    Something configured that this build's ladder no longer
                                    carries. Shown, rather than quietly swapped for a client
                                    nobody chose.
                                -->
                                <option value={config.YouTubeClient}>{config.YouTubeClient} (not in this build)</option>
                            {/if}
                        </select>
                    {/if}
                    <p class="note">
                        Only change this if trailers stop working; what YouTube hands over differs
                        by client and by day.
                    </p>
                </div>

                <!--
                    Under the settings rather than beside them: this is a statement about the
                    server, like everything else in this column, and the cards on the right are
                    about what is happening right now.
                -->
                <div class="field">
                    <span class="label">Plugins LiteTV leans on</span>
                    {#if siblings === null}
                        <p class="note">Asking…</p>
                    {:else if siblings.length === 0}
                        <p class="note">The server did not say.</p>
                    {:else}
                        <div class="siblings">
                            {#each siblings as plugin (plugin.Id)}
                                <div class="sibling">
                                    <span
                                        class="dot"
                                        class:on={plugin.Usable}
                                        class:half={plugin.Installed && !plugin.Usable}
                                    ></span>
                                    <div class="what">
                                        <div class="line">
                                            <span class="plugin-name">{plugin.Name}</span>
                                            <span class="state">
                                                {#if plugin.Usable}
                                                    {plugin.Version ?? 'installed'}
                                                {:else if plugin.Installed}
                                                    <!-- Installed and silent is its own state, and
                                                         the one worth naming: it looks like
                                                         working. -->
                                                    {plugin.Status ?? 'not answering'}
                                                {:else}
                                                    not installed
                                                {/if}
                                            </span>
                                        </div>
                                        <!-- Only when there is one. An empty line still takes
                                             its margin, which reads as a row that lost its
                                             description rather than one that never had it. -->
                                        {#if plugin.WhyItMatters}
                                            <p class="why">{plugin.WhyItMatters}</p>
                                        {/if}
                                    </div>
                                </div>
                            {/each}
                        </div>
                    {/if}
                </div>
            </div>

            <div class="right">
                <Card>
                    <h3>{german ? 'Trailerqualität' : 'Trailer quality'}</h3>
                    <div class="pair">
                        <span class="key">{german ? 'Herkunftsnachweis' : 'Proof of origin'}</span>
                        <span class="value">{poLine}</span>
                    </div>
                    <div class="pair">
                        <span class="key">{german ? 'Letzte Auflösung' : 'Last resolved'}</span>
                        {#if po?.LastResolved}
                            <span class="value" class:low={po.LastResolvedLow}>{po.LastResolved}</span>
                        {:else}
                            <span class="value dim">{german ? 'seit dem Serverstart nichts aufgelöst' : 'nothing resolved since this server started'}</span>
                        {/if}
                    </div>

                    {#if po?.LastResolvedLow}
                        <p class="low-note">
                            That came out below 720p. Anything resolved before a television minted
                            a token is re-requested automatically once one arrives — the cache is
                            keyed on the token — so this reading improves by itself after a mint.
                            A low reading <em>with</em> a token held is worth chasing.
                        </p>
                    {/if}
                </Card>

                <Card>
                    <h3>{german ? 'Wo die Kanäle abgespielt werden' : 'Where channels play'}</h3>
                    <p class="prose">
                        Channels are handed to the television by this server. Nothing here changes
                        what a client shows — that is the app's own business.
                    </p>
                </Card>
            </div>
        </div>
    {:else}
        <div class="body">
            <div class="left">
                <h2>{german ? 'Vom Server bereitgestellte TV-Versionen' : 'Builds this server hands the television'}</h2>

                {#if buildsError}
                    <p class="bad">The store could not be read: {buildsError}</p>
                {:else if !builds}
                    <p class="none">Looking…</p>
                {:else}
                    <div class="builds">
                        {#each builds.Builds as build (build.FileName)}
                            <div class="build">
                                <div class="who">
                                    <div class="top">
                                        <span class="tag quiet">{build.Family}</span>
                                        <span class="name">{build.Version}</span>
                                        {#if build.Abi}<span class="tag">{build.Abi}</span>{/if}
                                        <span class="tag quiet">{build.BuildType}</span>
                                        {#if build.Offered}
                                            <span class="tag on-offer">on offer</span>
                                        {/if}
                                    </div>
                                    <div class="file">{build.FileName}</div>
                                    <!-- Asked for: when this build arrived, in full and in words. -->
                                    <div class="when">
                                        Uploaded {uploadedAt(build.Modified)}
                                        <span class="ago">· {howLongAgo(build.Modified)}</span>
                                    </div>
                                </div>
                                <span class="size">{megabytes(build.Bytes)}</span>
                                <button type="button" class="bin" onclick={() => removeBuild(build.FileName)} aria-label="Delete this build">✕</button>
                            </div>
                        {:else}
                            <p class="none">Nothing in the store — the app has nothing to update to.</p>
                        {/each}
                    </div>

                    {#if supplanted.length > 0}
                        <button
                            type="button"
                            class="clear-out"
                            onclick={clearOutOldBuilds}
                            disabled={clearing}
                        >
                            {clearing
                                ? 'Removing...'
                                : 'Remove the ' + supplanted.length
                                    + (supplanted.length === 1 ? ' build' : ' builds')
                                    + ' nobody is offered'}
                        </button>
                        <p class="note small">
                            Everything not currently on offer - Wholphin
                            {builds.LatestVersion ? ' (' + builds.LatestVersion + ')' : ''} and
                            Findroid{builds.FindroidLatestVersion ? ' (' + builds.FindroidLatestVersion + ')' : ''}
                            each keep the build their own app is actually handed.
                        </p>
                    {/if}

                    <label class="upload">
                        <input
                            type="file"
                            accept=".apk"
                            multiple
                            onchange={(e) => {
                                const files = e.currentTarget.files;
                                if (files && files.length > 0) { void upload(files); }
                                e.currentTarget.value = '';
                            }}
                        />
                        <span class="lt-swap">
                            <span class="lt-ghost">Uploading 99/99…</span>
                            <span>{uploading ? `Uploading ${uploadDone}/${uploadTotal}…` : 'Upload builds'}</span>
                        </span>
                    </label>
                {/if}
            </div>

            <div class="right">
                <Card>
                    <h3>Put this in the app</h3>
                    <!--
                        Its own row with room to be read and selected. The old page clipped this
                        away inside a 69px card, which is the one outright bug the first real
                        server found.
                    -->
                    <p class="prose small">Wholphin - Settings › Updates › Update source, on the television.</p>
                    <input class="url" readonly value={builds?.UpdateUrl ?? ''} onclick={(e) => e.currentTarget.select()} />
                    <p class="prose small">Findroid - Settings › About › Update URL, on the phone.</p>
                    <input class="url" readonly value={builds?.FindroidUpdateUrl ?? ''} onclick={(e) => e.currentTarget.select()} />
                </Card>
            </div>
        </div>
    {/if}
</div>

<style>
    .screen { flex-grow: 1; min-height: 0; display: flex; flex-direction: column; }

    header { display: flex; align-items: center; gap: 13px; padding: 16px 22px 0; }
    h1 { font-size: 21px; font-weight: 700; color: var(--lt-text-strong); margin: 0; }
    .spacer { flex-grow: 1; }

    .save {
        padding: 6px 14px;
        border-radius: var(--lt-radius-small);
        border: 1px solid var(--lt-accent);
        background: var(--lt-accent);
        color: #fff;
        font-size: 12.5px;
        font-weight: 600;
        font-family: inherit;
        cursor: pointer;
    }

    .save:disabled { background: none; border-color: var(--lt-line); color: var(--lt-text-faint); cursor: default; }

    .subtabs {
        display: flex;
        gap: 9px;
        padding: 13px 22px;
        border-bottom: 1px solid var(--lt-line);
    }

    .subtabs button {
        padding: 7px 14px;
        border-radius: var(--lt-radius-small);
        font-size: 13.5px;
        font-weight: 600;
        font-family: inherit;
        background: var(--lt-card);
        border: 1px solid var(--lt-line);
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .subtabs button.on {
        background: var(--lt-accent);
        border-color: var(--lt-accent);
        color: #fff;
        box-shadow: 0 4px 12px var(--lt-accent-glow);
    }

    .body {
        flex-grow: 1;
        min-height: 0;
        padding: 22px;
        display: flex;
        gap: 30px;
        overflow: hidden;
    }

    .left { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 20px; overflow-y: auto; }
    .right { flex: 0 0 360px; display: flex; flex-direction: column; gap: 15px; overflow-y: auto; }

    .siblings { display: flex; flex-direction: column; gap: 11px; }

    .sibling { display: flex; gap: 9px; align-items: flex-start; }

    /* Three states, three colours: answering, there but silent, absent. */
    .dot {
        flex: 0 0 auto;
        width: 8px;
        height: 8px;
        margin-top: 4px;
        border-radius: 50%;
        background: var(--lt-text-faint);
    }

    .dot.on { background: #2f9e8f; }
    .dot.half { background: #d99a3a; }

    .sibling .what { min-width: 0; }

    .sibling .line { display: flex; align-items: baseline; gap: 7px; flex-wrap: wrap; }

    .plugin-name { font-size: 12.5px; font-weight: 600; color: var(--lt-text-title); }

    .state { font-size: 11px; color: var(--lt-text-dim); }

    .why { margin: 2px 0 0; font-size: 11.5px; color: var(--lt-text-dim); line-height: 1.45; }

    h2 { font-size: 15px; font-weight: 700; color: var(--lt-text-strong); margin: 0; }
    h3 { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin: 0 0 9px; }

    .field { display: flex; flex-direction: column; gap: 6px; }
    .label-row { display: flex; align-items: center; gap: 9px; }
    .label { font-size: 14px; font-weight: 600; color: var(--lt-text-title); }

    .text, .url {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 15px;
        font-family: inherit;
        color: var(--lt-text);
        max-width: 340px;
    }

    .url { max-width: none; width: 100%; font-size: 12.5px; }

    .note {
        font-size: 12.5px;
        color: var(--lt-text-muted);
        padding-left: 14px;
        border-left: 2px solid var(--lt-line);
        margin: 0;
    }

    .deeper {
        margin: 2px 0 0;
        padding: 11px 15px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-accent-soft);
        border-left: 2px solid var(--lt-accent);
        font-size: 13px;
        line-height: 1.5;
        color: rgba(255, 255, 255, .72);
        max-width: 580px;
    }

    .help {
        width: 18px;
        height: 18px;
        border-radius: 50%;
        border: 1px solid var(--lt-line-strong);
        background: none;
        color: var(--lt-text-dim);
        font-size: 11px;
        font-weight: 700;
        font-family: inherit;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        padding: 0;
    }

    .help.on { border-color: var(--lt-accent); color: var(--lt-accent); }

    .check { display: flex; align-items: center; gap: 9px; font-size: 13px; color: var(--lt-text-muted); }

    .pair { display: flex; gap: 12px; padding: 4px 0; font-size: 12.5px; }
    .key { flex: 0 0 118px; color: var(--lt-text-muted); }
    .value { color: var(--lt-text-title); }
    .value.dim { color: var(--lt-text-dim); }
    .value.low { color: var(--lt-collection); }

    .low-note {
        margin: 8px 0 0;
        padding: 9px 12px;
        border-radius: var(--lt-radius-small);
        background: rgba(217, 154, 58, .08);
        border-left: 2px solid var(--lt-collection);
        font-size: 12px;
        line-height: 1.5;
        color: var(--lt-text-muted);
    }

    .prose { font-size: 12.5px; line-height: 1.55; color: var(--lt-text-muted); margin: 0; }
    .prose.small { font-size: 12px; color: var(--lt-text-dim); margin-top: 9px; }

    .builds { display: flex; flex-direction: column; border: 1px solid var(--lt-line); border-radius: var(--lt-radius); overflow: hidden; }

    .build {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 10px 13px;
        border-bottom: 1px solid var(--lt-line-soft);
        background: var(--lt-card);
    }

    .who { flex-grow: 1; min-width: 0; }
    .top { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .name { font-size: 13.5px; font-weight: 600; color: var(--lt-text-title); }

    .tag {
        padding: 2px 8px;
        border-radius: 999px;
        background: rgba(255, 255, 255, .07);
        color: var(--lt-text-muted);
        font-size: 10.5px;
        font-weight: 700;
    }

    .tag.quiet { color: var(--lt-text-dim); }
    .tag.on-offer { background: rgba(119, 91, 244, .2); color: #9b8bf7; }

    .file { font-size: 11.5px; color: var(--lt-text-dim); margin-top: 2px; word-break: break-all; }
    .when { font-size: 11.5px; color: var(--lt-text-muted); margin-top: 3px; }
    .ago { color: var(--lt-text-dim); }

    .size { flex: 0 0 auto; font-size: 12px; color: var(--lt-text-dim); }

    .bin { background: none; border: none; color: var(--lt-text-dim); cursor: pointer; font-size: 12px; }

    .clear-out {
        align-self: flex-start;
        margin-top: 12px;
        padding: 7px 13px;
        border-radius: var(--lt-radius-small);
        border: 1px solid rgba(217, 154, 58, .35);
        background: rgba(217, 154, 58, .12);
        color: var(--lt-collection);
        font-family: inherit;
        font-size: 12.5px;
        cursor: pointer;
    }

    .clear-out:disabled { opacity: .6; cursor: default; }

    .note.small { font-size: 11.5px; margin-top: 5px; }

    .link {
        background: none;
        border: none;
        padding: 0;
        font: inherit;
        color: #9b8bf7;
        cursor: pointer;
        text-decoration: underline;
    }

    /*
        Left as the browser draws it, so it is obviously a list to choose from - but on an
        OPAQUE ground. `.text` above sets a translucent white, which the browser composites on
        its own light ground when it opens the list, and the result was white on white.
    */
    select.text { appearance: auto; background-color: var(--lt-field-solid); }
    .bin:hover { color: #e08585; }

    .upload {
        align-self: flex-start;
        padding: 8px 15px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-accent);
        color: #fff;
        font-size: 13px;
        font-weight: 600;
        cursor: pointer;
        box-shadow: 0 4px 12px var(--lt-accent-glow);
    }

    .upload input { display: none; }

    .none, .bad { margin: 0; font-size: 12.5px; padding: 8px 0; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
