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
    import { api, authHeaders, dashboard } from '../lib/jellyfin';

    interface PoToken {
        Held: boolean;
        MintedUtc: string | null;
        AgeSeconds: number | null;
        HasPlayerToken: boolean;
        /** What the last resolution produced, or null if nothing has been resolved yet. */
        LastResolved: string | null;
        LastResolvedLow: boolean;
    }

    interface Build {
        FileName: string;
        Version: string;
        BuildType: string;
        Abi: string | null;
        Bytes: number;
        /** When it was uploaded. */
        Modified: string;
    }

    interface BuildList {
        LatestVersion: string | null;
        UpdateUrl: string;
        Builds: Build[];
    }

    let pane = $state<'playback' | 'updates'>('playback');
    let po = $state<PoToken | null>(null);
    let builds = $state<BuildList | null>(null);
    let buildsError = $state<string | null>(null);
    let uploading = $state(false);
    let accountHelp = $state(false);
    let skipHelp = $state(false);

    const config = $derived(store.config);

    $effect(() => {
        api().getJSON<PoToken>(api().getUrl('LiteTv/PoToken'))
            .then((answer) => (po = answer))
            .catch(() => (po = null));
    });

    async function loadBuilds(): Promise<void> {
        buildsError = null;
        try {
            builds = await api().getJSON<BuildList>(api().getUrl('LiteTv/Update/Builds'));
        } catch (err) {
            buildsError = err instanceof Error ? err.message : String(err);
        }
    }

    $effect(() => {
        if (pane === 'updates' && builds === null && buildsError === null) {
            void loadBuilds();
        }
    });

    async function upload(file: File): Promise<void> {
        const bar = dashboard();
        uploading = true;
        try {
            const answer = await fetch(
                api().getUrl('LiteTv/Update/Builds/' + encodeURIComponent(file.name)),
                { method: 'POST', headers: authHeaders(), body: file },
            );
            if (!answer.ok) { throw new Error(answer.status + ' ' + answer.statusText); }
            await loadBuilds();
        } catch (err) {
            bar.alert('That build could not be uploaded: ' + (err instanceof Error ? err.message : String(err)));
        } finally {
            uploading = false;
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
            dashboard().alert(err instanceof Error ? err.message : String(err));
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

    function howLongAgo(value: string): string {
        const at = new Date(value).getTime();
        if (Number.isNaN(at)) { return ''; }
        const minutes = Math.round((Date.now() - at) / 60000);
        if (minutes < 1) { return 'just now'; }
        if (minutes < 60) { return minutes + ' min ago'; }
        const hours = Math.round(minutes / 60);
        if (hours < 24) { return hours + (hours === 1 ? ' hour ago' : ' hours ago'); }
        const days = Math.round(hours / 24);
        return days + (days === 1 ? ' day ago' : ' days ago');
    }

    const poLine = $derived.by(() => {
        if (!po) { return 'not known'; }
        if (!po.Held) { return 'none held — trailers are capped at 360p'; }
        const age = po.AgeSeconds === null ? '' : ' · minted ' + Math.round(po.AgeSeconds / 60) + ' min ago';
        return 'held' + age + (po.HasPlayerToken ? ' · with a player token' : '');
    });
</script>

<div class="screen">
    <header>
        <h1>Server settings</h1>
        <div class="spacer"></div>
        <button type="button" class="save" disabled={!store.dirty} onclick={() => store.save()}>Save</button>
    </header>

    <nav class="subtabs">
        <button type="button" class:on={pane === 'playback'} onclick={() => (pane = 'playback')}>Playback &amp; trailers</button>
        <button type="button" class:on={pane === 'updates'} onclick={() => (pane = 'updates')}>App updates</button>
    </nav>

    {#if !config}
        <p class="none">Loading…</p>
    {:else if pane === 'playback'}
        <div class="body">
            <div class="left">
                <div class="field">
                    <div class="label-row">
                        <span class="label">Channel playback account</span>
                        <button type="button" class="help" class:on={accountHelp} onclick={() => (accountHelp = !accountHelp)} aria-label="About the playback account">?</button>
                    </div>
                    <input class="text" bind:value={config.ChannelUserName} />
                    <p class="note">Channel viewing is recorded against this account, never yours.</p>
                    {#if accountHelp}
                        <p class="deeper">A channel plays with this account's token, so what it watches lands on its watch history and not on the account of whoever is looking. That is the whole reason it exists.</p>
                    {/if}
                </div>

                <div class="field">
                    <div class="label-row">
                        <span class="label">Skip the parts of a trailer that are not the trailer</span>
                        <button type="button" class="help" class:on={skipHelp} onclick={() => (skipHelp = !skipHelp)} aria-label="About skipping">?</button>
                    </div>
                    <label class="check">
                        <input
                            type="checkbox"
                            checked={config.SkipTrailerSegments}
                            onchange={(e) => { config.SkipTrailerSegments = e.currentTarget.checked; }}
                        />
                        <span>Ask SponsorBlock and skip what it names</span>
                    </label>
                    {#if skipHelp}
                        <p class="deeper">The uploader's branded card and the plea to subscribe are not the trailer. With this on, they are skipped and a break is sized by what actually plays.</p>
                    {/if}
                </div>

                <div class="field">
                    <span class="label">Ask YouTube as</span>
                    <input class="text" bind:value={config.YouTubeClient} placeholder="default" />
                    <p class="note">
                        Only change this if trailers stop working; what YouTube hands over differs
                        by client and by day.
                    </p>
                </div>
            </div>

            <div class="right">
                <Card>
                    <h3>Trailer quality</h3>
                    <div class="pair">
                        <span class="key">Proof of origin</span>
                        <span class="value">{poLine}</span>
                    </div>
                    <div class="pair">
                        <span class="key">Last resolved</span>
                        {#if po?.LastResolved}
                            <span class="value" class:low={po.LastResolvedLow}>{po.LastResolved}</span>
                        {:else}
                            <span class="value dim">nothing resolved since this server started</span>
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
                    <h3>Where channels play</h3>
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
                <h2>Builds this server hands the television</h2>

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
                                        <span class="name">{build.Version}</span>
                                        {#if build.Abi}<span class="tag">{build.Abi}</span>{/if}
                                        <span class="tag quiet">{build.BuildType}</span>
                                        {#if builds.LatestVersion === build.Version}
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

                    <label class="upload">
                        <input
                            type="file"
                            accept=".apk"
                            onchange={(e) => {
                                const file = e.currentTarget.files?.[0];
                                if (file) { void upload(file); }
                                e.currentTarget.value = '';
                            }}
                        />
                        {uploading ? 'Uploading…' : 'Upload a build'}
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
                    <input class="url" readonly value={builds?.UpdateUrl ?? ''} onclick={(e) => e.currentTarget.select()} />
                    <p class="prose small">Settings › Updates › Update source, on the television.</p>
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
