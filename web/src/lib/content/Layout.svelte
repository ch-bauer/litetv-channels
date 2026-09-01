<script lang="ts">
    /*
        "How they are laid out" - the design puts Order and Interleave here, beside the sources
        they act on. They used to live on Settings, which the Settings board itself says is
        wrong: "Anchor, episode interleaving and shuffle live under Content, beside the sources
        they act on."
    */
    import Card from '../ui/Card.svelte';
    import { api } from '../jellyfin';
    import { store } from '../config.svelte';
    import type { PlayOrder, TvChannel } from '../types';

    let { channel }: { channel: TvChannel } = $props();

    let helpOpen = $state(false);
    let cycle = $state<string | null>(null);
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    /*
        How long the channel takes to play everything once.

        The looping was never missing - a channel is `airtime % TotalTicks` and has always played
        through and begun again. What could not be seen was the SCALE: a channel built from one
        long series runs for months before it repeats, and one built from four films repeats
        before the evening is out. The server works it out and says it in words.

        It reads the SAVED channel, so it lags an unsaved edit by a Save - which is honest, since
        that is also when the change reaches the schedule.
    */
    $effect(() => {
        const id = channel.Id;
        // Re-asked after every Save, because the answer is about the saved channel: adding four
        // films and watching the line go on saying "Nothing to play yet" is what the owner saw.
        void store.savedAt;
        api().getJSON<{ Words: string }>(api().getUrl('LiteTv/Channels/' + id + '/Cycle'))
            .then((answer) => { cycle = answer.Words; })
            .catch(() => { cycle = null; });
    });

    /*
        The server counts what it HOLDS. So a line that says nothing is playable has to say
        whether that is a fact about the channel or a fact about the Save button - the two look
        identical, and one of them is not a fault at all.
    */
    const cycleLine = $derived.by(() => {
        if (!cycle) { return null; }
        const nothing = cycle.startsWith('Nothing to play');
        if (nothing && channel.Sources.length > 0) {
            return store.dirty
                ? (german ? 'Noch nichts zum Abspielen gespeichert — dieser Inhalt wurde noch nicht gespeichert. Speichere ihn, dann zeigt der Kanal seine Laufzeit.' : 'Nothing saved to play yet - this content has not been saved. Save, and the channel says how long it takes to play through.')
                : (german ? 'Hier kann nichts abgespielt werden: Die Bibliothek liefert für keinen Titel eine Laufzeit.' : 'Nothing here can be played: the library gave no runtime for any of it.');
        }
        if (store.dirty) {
            return cycle + (german ? ' Gezählt anhand der gespeicherten Daten — deine Änderungen sind noch nicht enthalten.' : ' Counted from what is saved, so it does not include your changes yet.');
        }
        return cycle;
    });

    /** Interleaving off means each source plays right through before the next begins. */
    const rightThrough = $derived((channel.EpisodesPerBlock || 0) <= 0);

    function setRightThrough(on: boolean): void {
        channel.EpisodesPerBlock = on ? 0 : 2;
    }

    const HELP = `A channel takes from each of its sources in turn.

With one at a time it plays one thing from the first source, then one from the next, and so on round the list.

With two, it plays two before moving on - which is what makes a channel that airs a pair of episodes and then a film, rather than a whole series and then a whole film.`;

    const orderNote = $derived(
        channel.Order === 'Shuffle'
            ? (german ? 'Einmal gemischt und beibehalten. Ein Zeitplan, der sich neu mischt, ist kein Zeitplan.' : 'Shuffled once, and kept. A schedule that re-draws itself is not a schedule.')
            : (german ? 'Jede Quelle nacheinander, in der Reihenfolge der Liste.' : 'Each source in turn, in the order of the list.'),
    );

    const interleaveNote = $derived(
        rightThrough
            ? (german ? 'Jede Quelle vollständig — eine ganze Serie läuft, bevor die nächste beginnt.' : 'Each source in full — a whole series plays through before the next begins.')
            : (channel.EpisodesPerBlock === 1
                ? (german ? 'Ein Titel aus jeder Quelle, bevor es zur nächsten geht.' : 'One from each source before moving to the next.')
                : channel.EpisodesPerBlock + (german ? ' Titel aus jeder Quelle, bevor es zur nächsten geht.' : ' from each source before moving to the next.')),
    );

    function pick(order: PlayOrder): void {
        if (channel.Order === order) { return; }
        channel.Order = order;
    }
</script>

<Card>
    <div class="stack">
        <h3>{german ? 'Wiedergabereihenfolge' : 'How they are laid out'}</h3>

        <div class="group">
            <div class="label">{german ? 'Reihenfolge' : 'Order'}</div>
            <div class="segmented" role="group" aria-label={german ? 'Wiedergabereihenfolge' : 'Play order'}>
                <button
                    type="button"
                    class:on={channel.Order !== 'Shuffle'}
                    aria-pressed={channel.Order !== 'Shuffle'}
                    onclick={() => pick('Sequential')}
                >{german ? 'In Reihenfolge' : 'In order'}</button>
                <button
                    type="button"
                    class:on={channel.Order === 'Shuffle'}
                    aria-pressed={channel.Order === 'Shuffle'}
                    onclick={() => pick('Shuffle')}
                >{german ? 'Zufällig' : 'Shuffled'}</button>
            </div>
            <p class="note">{orderNote}</p>
        </div>

        <div class="group">
            <div class="label">{german ? 'Wie weit abspielen' : 'How far through'}</div>
            <div class="segmented" role="group" aria-label={german ? 'Wie weit aus jeder Quelle abspielen' : 'How far through each source'}>
                <button type="button" class:on={rightThrough} aria-pressed={rightThrough} onclick={() => setRightThrough(true)}>
                    {german ? 'Komplett' : 'Right through'}
                </button>
                <button type="button" class:on={!rightThrough} aria-pressed={!rightThrough} onclick={() => setRightThrough(false)}>
                    {german ? 'Einige auf einmal' : 'A few at a time'}
                </button>
            </div>
            {#if cycleLine}
                <p class="cycle">{cycleLine}</p>
            {/if}
        </div>

        {#if !rightThrough}
        <div class="group">
            <div class="row">
                <div class="label">{german ? 'Abwechseln' : 'Interleave'}</div>
                <button
                    type="button"
                    class="help"
                    class:on={helpOpen}
                    aria-expanded={helpOpen}
                    aria-label={german ? 'Was das Abwechseln bewirkt' : 'What interleaving does'}
                    onclick={() => (helpOpen = !helpOpen)}
                >?</button>
            </div>

            <div class="row">
                <input
                    type="number"
                    min="1"
                    max="20"
                    value={channel.EpisodesPerBlock || 1}
                    oninput={(event) => {
                        const n = Number(event.currentTarget.value);
                        channel.EpisodesPerBlock = Number.isFinite(n) && n > 0 ? Math.floor(n) : 1;
                    }}
                    aria-label={german ? 'Wie viele auf einmal' : 'How many at a time'}
                />
                <span class="at">{german ? 'auf einmal' : 'at a time'}</span>
            </div>

            <p class="note">{interleaveNote}</p>

            {#if helpOpen}
                <p class="help-text">{HELP}</p>
            {/if}
        </div>
        {/if}
    </div>
</Card>

<style>
    .stack { display: flex; flex-direction: column; gap: 17px; }

    h3 {
        font-size: 13px;
        font-weight: 700;
        color: var(--lt-text-title);
        margin: 0;
    }

    .group { display: flex; flex-direction: column; gap: 7px; }

    .row { display: flex; align-items: center; gap: 9px; }

    .label {
        font-size: 13.5px;
        font-weight: 600;
        color: var(--lt-text-body);
    }

    .segmented {
        display: flex;
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        overflow: hidden;
    }

    .segmented button {
        flex: 1 1 0;
        text-align: center;
        padding: 7px 0;
        font-size: 12.5px;
        font-weight: 600;
        font-family: inherit;
        background: none;
        border: none;
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .segmented button:hover:not(.on) { background: var(--lt-hover); }

    .segmented button.on {
        background: var(--lt-accent);
        color: #fff;
    }

    input {
        flex: 0 0 62px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 7px 10px;
        font-size: 14px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .at { font-size: 12.5px; color: var(--lt-text-muted); }

    .help {
        width: 17px;
        height: 17px;
        border-radius: 50%;
        border: 1px solid var(--lt-line-strong);
        background: none;
        color: var(--lt-text-dim);
        font-size: 10.5px;
        font-weight: 700;
        font-family: inherit;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        padding: 0;
    }

    .help.on { border-color: var(--lt-accent); color: var(--lt-accent); }

    .cycle {
        margin: 0;
        font-size: 12px;
        color: var(--lt-text-muted);
        padding-left: 13px;
        border-left: 2px solid var(--lt-accent);
    }

    .note {
        font-size: 12px;
        color: var(--lt-text-muted);
        padding-left: 13px;
        border-left: 2px solid var(--lt-line);
        margin: 0;
    }

    .help-text {
        padding: 10px 13px;
        border-radius: var(--lt-radius-small);
        background: var(--lt-accent-soft);
        border-left: 2px solid var(--lt-accent);
        font-size: 12.5px;
        line-height: 1.5;
        color: rgba(255, 255, 255, 0.72);
        white-space: pre-line;
        margin: 0;
    }
</style>
