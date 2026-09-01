<script lang="ts">
    /*
        How often a channel stops, and how far ahead it is allowed to look for something to
        trail.

        These two lived on Settings, which the owner found wrong: they are about breaks, and
        breaks have their own screen. Nothing about them changed in the move except where they
        are - the wording, the ranges and the deeper notes are the ones that were written for
        them.
    */
    import type { TvChannel } from '../types';
    import { store } from '../config.svelte';

    let { channel }: { channel: TvChannel } = $props();
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

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

    let open = $state<Record<string, boolean>>({});

    const settings = $derived<Setting[]>([
        {
            key: 'trailerEvery',
            label: german ? 'Eine Pause alle' : 'A break every',
            unit: german ? 'Programme' : 'programmes',
            width: '90px',
            min: 0,
            max: 20,
            oneLine: german ? 'Wie oft der Kanal für einen Trailer pausiert.' : 'How often the channel stops for a trailer.',
            deeper: german ? `Null bedeutet nie: Der Kanal läuft direkt von einem Programm ins nächste.

Eine Pause enthält zuerst die Werbung und zuletzt den Trailer — sie endet also mit dem, was als Nächstes kommt. Der Inhalt dieser Pause steht auf dieser Seite.` : `Zero means never: the channel runs one programme straight into the next.

A break carries the channel's adverts first and the trailer last, so it ends on what is about to be shown. What goes in one is the list on this screen.`,
            get: () => channel.TrailerEveryPrograms,
            set: (v) => { channel.TrailerEveryPrograms = v; },
        },
        {
            key: 'lookahead',
            label: german ? 'Trailer ankündigen bis zu' : 'Trail something up to',
            unit: german ? 'Programme im Voraus' : 'programmes ahead',
            width: '90px',
            min: 2,
            max: 12,
            oneLine: german ? 'Wie weit der Trailer vorausblicken darf; 2 hält ihn vom nächsten Programm fern.' : 'How far ahead the trailer is allowed to look; 2 keeps it away from the next programme.',
            deeper: german ? `Ein Trailer kündigt etwas an, das der Kanal noch nicht gezeigt hat — selten ist es direkt das Nächste, denn das wäre keine echte Ankündigung.

So weit darf der Zeitplan nach einem passenden Trailer durchsucht werden.` : `A trailer announces something the channel has not shown yet, and it is rarely the very next thing - the next thing is minutes away, which is no announcement at all.

This is how far down the schedule it may reach to find something worth trailing.`,
            get: () => channel.TrailerLookahead,
            set: (v) => { channel.TrailerLookahead = v; },
        },
    ]);

    function change(setting: Setting, raw: string): void {
        const value = Number(raw);
        if (!Number.isFinite(value)) { return; }
        setting.set(Math.min(setting.max, Math.max(setting.min, Math.round(value))));
    }
</script>

<div class="cadence">
    {#each settings as setting (setting.key)}
        <div class="field">
            <div class="label-row">
                <span class="label">{setting.label}</span>
                <button
                    type="button"
                    class="help"
                    class:on={open[setting.key]}
                    aria-expanded={!!open[setting.key]}
                    aria-label={german ? 'Mehr über ' + setting.label : 'More about ' + setting.label}
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

<style>
    .cadence { display: flex; flex-direction: column; gap: 18px; }

    .field { display: flex; flex-direction: column; gap: 6px; }

    .label { font-size: 14px; font-weight: 600; color: var(--lt-text-title); }

    .label-row { display: flex; align-items: center; gap: 9px; }

    .number {
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 15px;
        font-family: inherit;
        color: var(--lt-text);
    }

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
</style>
