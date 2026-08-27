<script lang="ts">
    /*
        Breaks: the adverts a channel plays before what it is about to show.

        The screen's whole argument is that a length is measured, never typed - so an advert row
        shows what it will actually PLAY for and what was skipped out of it, and the add row asks
        for an address and a name and nothing else.

        The design's step-through ("Show what happens while it works it out") is here too: it is
        the only place the two halves of the answer - YouTube's length and SponsorBlock's
        segments - are visible separately, which is what makes a failure reportable instead of
        just a smaller number.
    */
    import Card from '../lib/ui/Card.svelte';
    import Note from '../lib/ui/Note.svelte';
    import SectionTitle from '../lib/ui/SectionTitle.svelte';
    import { mmss, resolveDuration, skipNote, type Duration } from '../lib/api/duration';
    import type { TvChannel } from '../lib/types';

    interface Advert {
        Name: string;
        Url: string;
        DurationSeconds: number;
        Decade: number;
        Enabled: boolean;
    }

    let { channel }: { channel: TvChannel } = $props();

    const adverts = $derived(channel.Adverts as Advert[]);

    let url = $state('');
    let name = $state('');
    let adding = $state(false);
    let addError = $state<string | null>(null);
    let stepThrough = $state(false);

    /** What the server said about each address, kept by address so a row can show it. */
    let measured = $state<Record<string, Duration>>({});
    let measuring = $state<Record<string, boolean>>({});

    async function measure(address: string): Promise<Duration | null> {
        if (measured[address]) { return measured[address]; }
        measuring[address] = true;
        try {
            const answer = await resolveDuration(address);
            measured[address] = answer;
            return answer;
        } catch {
            return null;
        } finally {
            measuring[address] = false;
        }
    }

    // Every advert already on the channel gets measured once, so the list shows real lengths
    // rather than the stored guess.
    $effect(() => {
        for (const advert of adverts) {
            if (advert.Url && !measured[advert.Url] && !measuring[advert.Url]) {
                void measure(advert.Url);
            }
        }
    });

    async function add(): Promise<void> {
        const address = url.trim();
        if (address.length === 0) { return; }
        adding = true;
        addError = null;
        try {
            const answer = await measure(address);
            if (!answer || answer.PlayableSeconds <= 0) {
                // Said out loud rather than stored as a zero that quietly plays nothing.
                addError = answer && answer.LengthSeconds <= 0
                    ? 'YouTube would not say how long that is, so it cannot be scheduled.'
                    : 'Nothing is left of that once the skips are taken out.';
                return;
            }
            adverts.push({
                Name: name.trim() || (answer.VideoId ?? address),
                Url: address,
                DurationSeconds: answer.PlayableSeconds,
                Decade: 0,
                Enabled: true,
            });
            url = '';
            name = '';
        } finally {
            adding = false;
        }
    }

    function remove(index: number): void {
        adverts.splice(index, 1);
    }

    function lengthOf(advert: Advert): string {
        const answer = measured[advert.Url];
        if (!answer) { return measuring[advert.Url] ? '…' : mmss(advert.DurationSeconds); }
        return mmss(answer.PlayableSeconds);
    }

    function noteOf(advert: Advert): { text: string; good: boolean } {
        const answer = measured[advert.Url];
        if (!answer) {
            return measuring[advert.Url]
                ? { text: 'working it out…', good: true }
                : { text: 'not measured yet', good: false };
        }
        return skipNote(answer);
    }

    /** The worked example on the right: a break built from what is actually here. */
    const breakdown = $derived.by(() => {
        const rows: { clock: string; label: string; length: string; fill: string }[] = [];
        let at = 0;
        for (const advert of adverts.slice(0, 3)) {
            const seconds = measured[advert.Url]?.PlayableSeconds ?? advert.DurationSeconds;
            rows.push({
                clock: mmss(at),
                label: advert.Name,
                length: mmss(seconds),
                fill: '#2f9e8f',
            });
            at += seconds;
        }
        rows.push({
            clock: mmss(at),
            label: 'Trailer for what is on next',
            length: '~2:30',
            fill: '#d99a3a',
        });
        return rows;
    });
</script>

<div class="screen">
    <div class="left">
        <div>
            <SectionTitle>Adverts</SectionTitle>
            <div class="spaced">
                <Note>
                    Addresses the television resolves and plays. They go at the front of a break,
                    with the trailer last, so a break ends on what the channel is about to show.
                </Note>
            </div>
        </div>

        <Card flush>
            {#each adverts as advert, index (advert.Url + ':' + index)}
                {@const note = noteOf(advert)}
                <div class="row">
                    <span class="edge"></span>
                    <div class="who">
                        <div class="top">
                            <span class="name" title={advert.Name}>{advert.Name}</span>
                            {#if advert.Decade > 0}<span class="decade">{advert.Decade}s</span>{/if}
                        </div>
                        <div class="url" title={advert.Url}>{advert.Url}</div>
                    </div>
                    <div class="length">
                        <div class="plays">{lengthOf(advert)}</div>
                        <div class="skip" class:bad={!note.good}>{note.text}</div>
                    </div>
                    <button type="button" class="bin" onclick={() => remove(index)} aria-label="Remove this advert">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
                            <path d="M5 7h14M10 11v6M14 11v6M6 7l1 12.5a1.5 1.5 0 0 0 1.5 1.5h7a1.5 1.5 0 0 0 1.5-1.5L18 7M9.5 7V4.5A1.5 1.5 0 0 1 11 3h2a1.5 1.5 0 0 1 1.5 1.5V7" />
                        </svg>
                    </button>
                </div>
            {:else}
                <p class="none">No adverts — breaks are just the trailer.</p>
            {/each}

            <div class="add">
                <input
                    class="url-field"
                    type="url"
                    bind:value={url}
                    placeholder="Paste a YouTube address…"
                    aria-label="Address of the advert"
                />
                <input
                    class="name-field"
                    bind:value={name}
                    placeholder="Name (optional)"
                    aria-label="Name for this advert"
                />
                <button type="button" class="go" onclick={add} disabled={adding || url.trim().length === 0}>
                    {adding ? 'Measuring…' : 'Add'}
                </button>
            </div>

            {#if addError}
                <p class="add-note bad">{addError}</p>
            {:else if adding}
                <p class="add-note">Asking YouTube how long it is, and SponsorBlock what gets skipped…</p>
            {/if}
        </Card>

        <div>
            <button type="button" class="ghost" onclick={() => (stepThrough = !stepThrough)} aria-expanded={stepThrough}>
                {stepThrough ? 'Hide' : 'Show'} what happens while it works it out
            </button>
        </div>

        {#if stepThrough}
            <Card>
                <h3>What happens while it works it out</h3>
                {#each adverts as advert (advert.Url)}
                    {@const answer = measured[advert.Url]}
                    <div class="step">
                        <div class="step-name">{advert.Name}</div>
                        {#if !answer}
                            <div class="step-line">…still asking</div>
                        {:else}
                            <div class="step-line">
                                <span class="tag">YouTube</span>
                                {answer.LengthSeconds > 0
                                    ? 'the video is ' + mmss(answer.LengthSeconds) + ' long'
                                    : 'would not say how long it is'}
                            </div>
                            <div class="step-line">
                                <span class="tag">SponsorBlock</span>
                                {answer.SkipSegments.length === 0
                                    ? 'no segments to skip'
                                    : answer.SkipSegments.length + ' segments, ' + mmss(answer.SkippedSeconds) + ' in total'}
                            </div>
                            <div class="step-line strong">
                                <span class="tag">The break gets</span>
                                {mmss(answer.PlayableSeconds)}
                            </div>
                        {/if}
                    </div>
                {:else}
                    <p class="none">Nothing to work out yet.</p>
                {/each}
            </Card>
        {/if}
    </div>

    <div class="right">
        <Card>
            <h3>Why there is no length to type</h3>
            <p class="prose">
                A trailer from YouTube is rarely only the trailer. The uploader wraps it in a
                branded card and a plea to subscribe, and the television skips both — so the
                number that matters is not how long the video is, but how long it <em>plays</em> for.
            </p>
            <p class="prose">
                Nobody can type that. The plugin asks YouTube for the length and SponsorBlock for
                the parts that get skipped, and the break is sized by what is left.
            </p>
        </Card>

        <Card>
            <h3>A break, made of that</h3>
            <div class="breakdown">
                {#each breakdown as row (row.clock + row.label)}
                    <div class="brow">
                        <span class="at">{row.clock}</span>
                        <span class="bar" style="background: {row.fill}"></span>
                        <span class="what" title={row.label}>{row.label}</span>
                        <span class="len">{row.length}</span>
                    </div>
                {/each}
            </div>
        </Card>
    </div>
</div>

<style>
    .screen {
        flex-grow: 1;
        min-height: 0;
        padding: 20px 22px;
        display: flex;
        gap: 28px;
        overflow: hidden;
    }

    .left {
        flex: 1 1 0;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 16px;
        overflow-y: auto;
    }

    .right {
        flex: 0 0 400px;
        display: flex;
        flex-direction: column;
        gap: 15px;
        overflow-y: auto;
    }

    .spaced { margin-top: 6px; }

    .row {
        display: flex;
        align-items: center;
        gap: 13px;
        padding: 11px 14px;
        border-bottom: 1px solid var(--lt-line-soft);
    }

    .row:hover { background: var(--lt-hover); }

    .edge {
        flex: 0 0 3px;
        align-self: stretch;
        min-height: 2.2em;
        border-radius: 2px;
        background: #2f9e8f;
    }

    .who { flex-grow: 1; min-width: 0; }

    .top { display: flex; align-items: center; gap: 9px; }

    .name {
        font-size: 13.5px;
        font-weight: 600;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .decade {
        flex: 0 0 auto;
        padding: 1px 7px;
        border-radius: 999px;
        background: rgba(255, 255, 255, .07);
        font-size: 10.5px;
        font-weight: 700;
        color: var(--lt-text-muted);
    }

    .url {
        font-size: 11.5px;
        color: var(--lt-text-dim);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        margin-top: 2px;
    }

    .length { flex: 0 0 auto; text-align: right; }

    .plays { font-size: 14px; font-weight: 700; color: var(--lt-text-title); }

    .skip { font-size: 11px; color: #6ea84f; margin-top: 1px; }
    .skip.bad { color: var(--lt-collection); }

    .bin {
        flex: 0 0 auto;
        background: none;
        border: none;
        padding: 2px;
        color: rgba(255, 255, 255, .35);
        cursor: pointer;
    }

    .bin:hover { color: #e08585; }

    .add {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 13px 14px;
        background: var(--lt-card-inset);
    }

    input {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .url-field { flex: 1 1 0; min-width: 0; }
    .name-field { flex: 0 1 150px; }

    .go {
        padding: 8px 14px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-accent);
        border: 1px solid var(--lt-accent);
        color: #fff;
        font-size: 13px;
        font-weight: 600;
        font-family: inherit;
        cursor: pointer;
    }

    .go:disabled {
        background: none;
        border-color: var(--lt-line-strong);
        color: var(--lt-text-faint);
        cursor: default;
    }

    .add-note {
        margin: 0;
        padding: 0 14px 13px;
        background: var(--lt-card-inset);
        font-size: 12.5px;
        color: var(--lt-text-muted);
    }

    .add-note.bad { color: #e08585; }

    .ghost {
        padding: 5px 11px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        font-size: 12px;
        font-family: inherit;
        color: var(--lt-text-muted);
        cursor: pointer;
    }

    h3 {
        font-size: 13px;
        font-weight: 700;
        color: var(--lt-text-title);
        margin: 0 0 5px;
    }

    .prose {
        font-size: 12.5px;
        line-height: 1.55;
        color: var(--lt-text-muted);
        margin: 0 0 10px;
    }

    .prose:last-child { margin-bottom: 0; }

    .breakdown { display: flex; flex-direction: column; gap: 7px; margin-top: 11px; }

    .brow { display: flex; align-items: stretch; gap: 10px; }

    .at { flex: 0 0 40px; font-size: 12px; font-weight: 700; color: rgba(255, 255, 255, .7); }

    .bar { flex: 0 0 3px; border-radius: 2px; min-height: 1.2em; }

    .what {
        flex-grow: 1;
        min-width: 0;
        font-size: 12.5px;
        color: var(--lt-text-muted);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .len { flex: 0 0 auto; font-size: 11.5px; color: var(--lt-text-dim); }

    .step { padding: 8px 0; border-bottom: 1px solid var(--lt-line-soft); }
    .step:last-child { border-bottom: none; }

    .step-name { font-size: 12.5px; font-weight: 600; color: var(--lt-text-title); margin-bottom: 4px; }

    .step-line { font-size: 12px; color: var(--lt-text-muted); margin-top: 2px; }
    .step-line.strong { color: var(--lt-text-title); font-weight: 600; }

    .tag {
        display: inline-block;
        min-width: 7.5em;
        color: var(--lt-text-dim);
    }

    .none { padding: 14px; margin: 0; font-size: 12.5px; color: var(--lt-text-dim); }
</style>
