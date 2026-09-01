<script lang="ts">
    /*
        Which channel — built for a lineup rather than a handful, as the board says.

        A filter above, the list scrolls, rows are one line each, and the count sits at the foot
        so "did that get added" is answerable without counting. Order is the owner's; nothing is
        regrouped underneath them by a channel going on or off air — the dot carries that.
    */
    import { store } from './config.svelte';
    import { week } from './week/weekStore.svelte';
    import type { TvChannel } from './types';

    let { destination = $bindable<'channel' | 'server' | 'suggest'>('channel') }:
        { destination?: 'channel' | 'server' | 'suggest' } = $props();

    let filter = $state('');

    const shown = $derived.by(() => {
        const needle = filter.trim().toLowerCase();
        if (needle.length === 0) { return store.channels; }
        return store.channels.filter((c) => c.Name.toLowerCase().includes(needle));
    });

    const count = $derived.by(() => {
        const all = store.channels.length;
        const german = store.config?.PageLanguage === 'de'
            || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de'));
        if (filter.trim().length === 0) {
            return all + (all === 1 ? (german ? ' Kanal' : ' channel') : (german ? ' Kanäle' : ' channels'));
        }
        return german ? shown.length + ' von ' + all : shown.length + ' of ' + all;
    });

    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto'
            && typeof navigator !== 'undefined'
            && navigator.language.toLowerCase().startsWith('de')));

    function pick(channel: TvChannel): void {
        store.channelId = channel.Id;
        destination = 'channel';
    }

    function add(): void {
        // Opens the new-channel screens. Starting from nothing is still there, one click in.
        destination = 'suggest';
    }
</script>

<!--
    Clicking the rail's own ground - beside the brand, under the last channel, anywhere that is
    not a control - lets go of whatever is selected on the screen beside it. The owner reported
    that letting go "works kinda, but only in the right side of the view": the grid cleared a
    selection and nothing else did, so the empty half of the window was dead to the mouse.
-->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<!-- svelte-ignore a11y_click_events_have_key_events -->
<nav class="rail" onclick={() => (week.selectedId = null)}>
    <div class="brand">
        <svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="var(--lt-accent)" stroke-width="1.8" aria-hidden="true">
            <rect x="2" y="7" width="20" height="14" rx="2.5" /><path d="m7 7 5-4 5 4" />
        </svg>
        <span class="name">LiteTV</span>
        <button type="button" class="add" onclick={add} title={german ? 'Neuer Kanal' : 'New channel'} aria-label={german ? 'Neuer Kanal' : 'New channel'}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9b8bf7" stroke-width="2.2" aria-hidden="true">
                <path d="M12 5v14M5 12h14" />
            </svg>
        </button>
    </div>

    <div class="filter">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="rgba(255,255,255,.4)" stroke-width="1.9" aria-hidden="true">
            <circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" />
        </svg>
        <input bind:value={filter} placeholder={german ? 'Kanäle filtern' : 'Filter channels'} aria-label={german ? 'Kanäle filtern' : 'Filter channels'} />
        {#if filter.length > 0}
            <button type="button" class="clear" onclick={() => (filter = '')} aria-label={german ? 'Filter löschen' : 'Clear the filter'}>✕</button>
        {/if}
    </div>

    <div class="list">
        {#each shown as channel (channel.Id)}
            <button
                type="button"
                class="row"
                class:on={destination === 'channel' && store.channel?.Id === channel.Id}
                onclick={() => pick(channel)}
            >
                <span class="dot" class:live={channel.Enabled}></span>
                <span class="who">
                    <span class="label">{channel.Name}</span>
                    <span class="sub">{channel.Sources.length
                        ? channel.Sources.length + (channel.Sources.length === 1 ? (german ? ' Quelle' : ' source') : (german ? ' Quellen' : ' sources'))
                        : (german ? 'nichts abzuspielen' : 'nothing to play')}</span>
                </span>
            </button>
        {:else}
            <p class="none">{store.channels.length === 0 ? (german ? 'Noch keine Kanäle.' : 'No channels yet.') : (german ? 'Keine Treffer.' : 'Nothing matches.')}</p>
        {/each}
    </div>

    <div class="count">{count}</div>

    <div class="destinations">
        <button type="button" class="dest" class:on={destination === 'server'} onclick={() => (destination = 'server')}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true">
                <circle cx="12" cy="12" r="2.8" />
                <path d="M9.7 3h4.6l.5 2.6 2.3 1.3 2.4-1 2.3 4-2 1.7v2.6l2 1.7-2.3 4-2.4-1-2.3 1.3-.5 2.8H9.7l-.5-2.8-2.3-1.3-2.4 1-2.3-4 2-1.7v-2.6l-2-1.7 2.3-4 2.4 1 2.3-1.3z" />
            </svg>
            {german ? 'Servereinstellungen' : 'Server settings'}
        </button>
    </div>
</nav>

<style>
    /*
        One width, always. `flex: 0 0 246px` is not enough on its own: a flex item's automatic
        minimum size is its content, so a long channel name could push the rail wider than its
        own basis - which is what "the rail changes width with the channel name" was. The
        minimum is stated, and every row inside is allowed to shrink and ellipsise instead.
    */
    .rail {
        flex: 0 0 246px;
        width: 246px;
        min-width: 246px;
        max-width: 246px;
        border-right: 1px solid var(--lt-line);
        display: flex;
        flex-direction: column;
        min-height: 0;
    }

    .brand {
        padding: 17px 16px 11px;
        display: flex;
        align-items: center;
        gap: 9px;
    }

    .brand .name {
        font-size: 17px;
        font-weight: 700;
        color: var(--lt-text-title);
        flex-grow: 1;
    }

    .add {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 24px;
        height: 24px;
        border-radius: var(--lt-radius-small);
        background: rgba(119, 91, 244, .16);
        border: 1px solid rgba(119, 91, 244, .3);
        cursor: pointer;
        padding: 0;
    }

    .filter {
        margin: 0 12px 10px;
        display: flex;
        align-items: center;
        gap: 7px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 6px 9px;
    }

    .filter input {
        flex-grow: 1;
        min-width: 0;
        background: none;
        border: none;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .filter input:focus { outline: none; }

    .clear {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        cursor: pointer;
        font-size: 11px;
        padding: 0;
    }

    .list {
        flex-grow: 1;
        min-height: 0;
        overflow-y: auto;
        padding: 0 8px;
        display: flex;
        flex-direction: column;
        gap: 1px;
    }

    .row {
        display: flex;
        min-width: 0;
        align-items: center;
        gap: 9px;
        padding: 7px 9px;
        border-radius: var(--lt-radius-small);
        border: none;
        background: none;
        font-family: inherit;
        text-align: left;
        cursor: pointer;
        width: 100%;
    }

    .row:hover { background: var(--lt-hover); }
    .row.on { background: rgba(119, 91, 244, .16); }

    .dot {
        flex: 0 0 auto;
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: var(--lt-text-faint);
    }

    .dot.live { background: var(--lt-accent); }

    .who { flex-grow: 1; min-width: 0; display: block; }

    .label {
        display: block;
        font-size: 13px;
        font-weight: 600;
        color: var(--lt-text-title);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .sub {
        display: block;
        font-size: 11px;
        color: var(--lt-text-dim);
        margin-top: 1px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .none { padding: 10px; margin: 0; font-size: 12px; color: var(--lt-text-dim); }

    .count {
        border-top: 1px solid var(--lt-line);
        padding: 8px 14px;
        font-size: 11px;
        color: var(--lt-text-dim);
    }

    .destinations {
        border-top: 1px solid var(--lt-line);
        padding: 8px;
        display: flex;
        flex-direction: column;
        gap: 2px;
    }

    .dest {
        display: flex;
        min-width: 0;
        align-items: center;
        gap: 10px;
        padding: 8px 11px;
        border-radius: var(--lt-radius-small);
        border: none;
        background: none;
        color: var(--lt-text-dim);
        font-size: 13px;
        font-family: inherit;
        cursor: pointer;
        text-align: left;
    }

    .dest:hover { background: var(--lt-hover); }
    .dest.on { background: rgba(119, 91, 244, .16); color: var(--lt-text-title); }
</style>
