<script lang="ts">
    /*
        Settings: what the channel is called, whether it is on, and the few numbers that shape a
        week the next time one is laid out.

        Anchor, interleaving and shuffle are deliberately NOT here - the board says so in as many
        words, and they now live on Content beside the sources they act on.
    */
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();

    // The break cadence and the evening it makes moved to the Breaks screen, where the owner
    // expects to find them. What is left here is what the channel IS rather than how it runs.
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
            />
            <p class="note">What the guide calls it on the television.</p>
        </div>

        <button
            type="button"
            class="onair"
            class:on={channel.Enabled}
            onclick={() => { channel.Enabled = !channel.Enabled; }}
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

    </div>

    <div class="right">
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
            Anchor, episode interleaving and shuffle live under Content, beside the sources they
            act on; how often the channel breaks lives under Breaks, beside what goes in one.
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
