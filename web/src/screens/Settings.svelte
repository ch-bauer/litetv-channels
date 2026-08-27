<script lang="ts">
    /*
        Settings: what the channel is called, whether it is on, and the few numbers that shape a
        week the next time one is laid out.

        Anchor, interleaving and shuffle are deliberately NOT here - the board says so in as many
        words, and they now live on Content beside the sources they act on.
    */
    import Card from '../lib/ui/Card.svelte';
    import { store } from '../lib/config.svelte';
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();

    let open = $state<Record<string, boolean>>({});

    interface Setting {
        key: string;
        label: string;
        unit: string;
        oneLine: string;
        deeper: string;
        width: string;
        min: number;
        max: number;
        get: () => number;
        set: (value: number) => void;
    }

    const settings: Setting[] = [
        {
            key: 'trailerEvery',
            label: 'A break every',
            unit: 'programmes',
            width: '90px',
            min: 0,
            max: 20,
            oneLine: 'How often the channel stops for a trailer.',
            deeper: `Zero means never: the channel runs one programme straight into the next.

A break carries the channel's adverts first and the trailer last, so it ends on what is about to be shown. What goes in one is on the Breaks tab.`,
            get: () => channel.TrailerEveryPrograms,
            set: (v) => { channel.TrailerEveryPrograms = v; },
        },
        {
            key: 'lookahead',
            label: 'Trail something up to',
            unit: 'programmes ahead',
            width: '90px',
            min: 1,
            max: 12,
            oneLine: 'How far ahead the trailer is allowed to look.',
            deeper: `A trailer announces something the channel has not shown yet, and it is rarely the very next thing - the next thing is minutes away, which is no announcement at all.

This is how far down the schedule it may reach to find something worth trailing.`,
            get: () => channel.TrailerLookahead,
            set: (v) => { channel.TrailerLookahead = v; },
        },
    ];

    function change(setting: Setting, raw: string): void {
        const value = Number(raw);
        if (!Number.isFinite(value)) { return; }
        setting.set(Math.min(setting.max, Math.max(setting.min, Math.round(value))));
        store.touch();
    }

    /** A typical evening, as the settings stand. */
    const preview = $derived.by(() => {
        const rows: { clock: string; label: string; fill: string }[] = [];
        let minutes = 20 * 60 + 15;
        const clock = (m: number) =>
            String(Math.floor(m / 60) % 24).padStart(2, '0') + ':' + String(m % 60).padStart(2, '0');

        const names = channel.Sources.length > 0
            ? channel.Sources.map((s) => s.Name)
            : ['Something from this channel'];

        const every = channel.TrailerEveryPrograms;
        for (let i = 0; i < 4; i++) {
            rows.push({ clock: clock(minutes), label: names[i % names.length], fill: '#5b6ee1' });
            minutes += 105;
            if (every > 0 && (i + 1) % every === 0) {
                rows.push({ clock: clock(minutes), label: 'Break — adverts, then a trailer', fill: '#d99a3a' });
                minutes += 5;
            }
        }
        return rows;
    });
</script>

<div class="screen">
    <div class="left">
        <div class="eyebrow">THE CHANNEL</div>

        <div class="field">
            <label class="label" for="channel-name">Name</label>
            <input
                id="channel-name"
                class="text"
                bind:value={channel.Name}
                oninput={() => store.touch()}
            />
            <p class="note">What the guide calls it on the television.</p>
        </div>

        <button
            type="button"
            class="onair"
            class:on={channel.Enabled}
            onclick={() => { channel.Enabled = !channel.Enabled; store.touch(); }}
            aria-pressed={channel.Enabled}
        >
            <span class="box">
                {#if channel.Enabled}
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="3.4" aria-hidden="true">
                        <path d="m5 13 4.5 4.5L19 7" />
                    </svg>
                {/if}
            </span>
            <span class="onair-text">
                <span class="label">On air</span>
                <span class="note-inline">A channel that is off stays in this list and shows on no client.</span>
            </span>
        </button>

        {#each settings as setting (setting.key)}
            <div class="field">
                <div class="label-row">
                    <span class="label">{setting.label}</span>
                    <button
                        type="button"
                        class="help"
                        class:on={open[setting.key]}
                        aria-expanded={!!open[setting.key]}
                        aria-label="More about {setting.label}"
                        onclick={() => (open[setting.key] = !open[setting.key])}
                    >?</button>
                </div>

                <div class="value-row">
                    <input
                        class="number"
                        style="flex: 0 0 {setting.width}"
                        type="number"
                        min={setting.min}
                        max={setting.max}
                        value={setting.get()}
                        oninput={(e) => change(setting, e.currentTarget.value)}
                        aria-label={setting.label}
                    />
                    <span class="unit">{setting.unit}</span>
                </div>

                <p class="note">{setting.oneLine}</p>

                {#if open[setting.key]}
                    <p class="deeper">{setting.deeper}</p>
                {/if}
            </div>
        {/each}
    </div>

    <div class="right">
        <Card>
            <h3>What this adds up to</h3>
            <p class="sub">A typical evening with the settings as they stand.</p>
            <div class="preview">
                {#each preview as row, index (index)}
                    <div class="prow">
                        <span class="at">{row.clock}</span>
                        <span class="bar" style="background: {row.fill}"></span>
                        <span class="what" title={row.label}>{row.label}</span>
                    </div>
                {/each}
            </div>
        </Card>

        <div class="warn">
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="#d99a3a" stroke-width="1.9" aria-hidden="true">
                <path d="M12 9v4.5M12 17h.01" />
                <path d="M10.3 3.9 2.4 17.5A2 2 0 0 0 4.1 20.5h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" />
            </svg>
            <div>
                <div class="warn-title">These only shape a new week</div>
                <p class="warn-text">
                    This channel’s week is written down, so changing anything here does nothing to
                    what is already scheduled. It takes effect when you lay the week out again — a
                    button on the Week tab, which discards what you arranged by hand.
                </p>
            </div>
        </div>

        <div class="footnote">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true">
                <circle cx="12" cy="12" r="9" /><path d="M12 8v5M12 16h.01" />
            </svg>
            Anchor, episode interleaving and shuffle live under Content, beside the sources they act on.
        </div>
    </div>
</div>

<style>
    .screen {
        flex-grow: 1;
        min-height: 0;
        padding: 22px;
        display: flex;
        gap: 30px;
        overflow: hidden;
    }

    .left { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 20px; overflow-y: auto; }
    .right { flex: 0 0 400px; display: flex; flex-direction: column; gap: 15px; overflow-y: auto; }

    .eyebrow {
        font-size: 10.5px;
        font-weight: 600;
        letter-spacing: .1em;
        color: var(--lt-text-dim);
    }

    .field { display: flex; flex-direction: column; gap: 6px; }

    .label { font-size: 14px; font-weight: 600; color: var(--lt-text-title); }

    .label-row { display: flex; align-items: center; gap: 9px; }

    .text, .number {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 15px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .text { max-width: 340px; }

    .value-row { display: flex; align-items: center; gap: 10px; }

    .unit { font-size: 13px; color: var(--lt-text-muted); }

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
        white-space: pre-line;
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

    .onair {
        display: flex;
        align-items: flex-start;
        gap: 11px;
        padding: 11px 13px;
        border-radius: var(--lt-radius-small);
        background: linear-gradient(90deg, rgba(119, 91, 244, .13) 0%, rgba(119, 91, 244, .02) 100%);
        border: 1px solid rgba(119, 91, 244, .2);
        font-family: inherit;
        text-align: left;
        cursor: pointer;
        max-width: 580px;
    }

    .box {
        flex: 0 0 auto;
        width: 18px;
        height: 18px;
        border-radius: 4px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        display: flex;
        align-items: center;
        justify-content: center;
        margin-top: 1px;
    }

    .onair.on .box { background: #4f46e5; border-color: #4f46e5; }

    .onair-text { display: block; }
    .note-inline { display: block; font-size: 12.5px; color: var(--lt-text-muted); }

    h3 { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin: 0 0 5px; }
    .sub { font-size: 12.5px; color: var(--lt-text-muted); margin: 0 0 13px; }

    .preview { display: flex; flex-direction: column; gap: 8px; }
    .prow { display: flex; align-items: stretch; gap: 11px; }
    .at { flex: 0 0 42px; font-size: 12.5px; font-weight: 700; color: rgba(255, 255, 255, .7); }
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

    .warn {
        border: 1px solid rgba(217, 154, 58, .25);
        border-radius: var(--lt-radius);
        padding: 14px 16px;
        background: rgba(217, 154, 58, .07);
        display: flex;
        gap: 11px;
    }

    .warn svg { flex: 0 0 auto; margin-top: 1px; }
    .warn-title { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin-bottom: 4px; }
    .warn-text { font-size: 12.5px; line-height: 1.5; color: var(--lt-text-muted); margin: 0; }

    .footnote {
        display: flex;
        align-items: flex-start;
        gap: 9px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .footnote svg { flex: 0 0 auto; margin-top: 1px; }
</style>
