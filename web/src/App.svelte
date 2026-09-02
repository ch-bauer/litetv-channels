<script lang="ts">
    import './lib/theme.css';
    import Rail from './lib/Rail.svelte';
    import { store } from './lib/config.svelte';
    import { week } from './lib/week/weekStore.svelte';
    import { hasDashboard, NoDashboardError } from './lib/jellyfin';
    import Week from './screens/Week.svelte';
    import Content from './screens/Content.svelte';
    import Breaks from './screens/Breaks.svelte';
    import Look from './screens/Look.svelte';
    import Settings from './screens/Settings.svelte';
    import Server from './screens/Server.svelte';
    import Suggest from './screens/Suggest.svelte';

    type Tab = 'week' | 'content' | 'breaks' | 'look' | 'settings';

    const TABS: { id: Tab; label: string; ready: boolean }[] = [
        { id: 'week', label: 'Week', ready: true },
        { id: 'content', label: 'Content', ready: true },
        { id: 'breaks', label: 'Breaks', ready: true },
        { id: 'look', label: 'Look', ready: true },
        { id: 'settings', label: 'Settings', ready: true },
    ];

    let tab = $state<Tab>('week');
    let destination = $state<'channel' | 'server' | 'suggest'>('channel');

    const connected = hasDashboard();
    if (connected) { void store.load(); }

    const channel = $derived(store.channel);
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage !== 'en'
            && typeof navigator !== 'undefined'
            && navigator.language.toLowerCase().startsWith('de')));

    const tabLabel = (id: Tab): string => german
        ? ({ week: 'Woche', content: 'Inhalt', breaks: 'Pausen', look: 'Aussehen', settings: 'Einstellungen' }[id])
        : ({ week: 'Week', content: 'Content', breaks: 'Breaks', look: 'Look', settings: 'Settings' }[id]);

    $effect(() => {
        if (typeof document !== 'undefined') {
            document.documentElement.lang = german ? 'de' : 'en';
        }
    });

    /* Configuration edits save automatically. The week remains an explicit draft because its
       timeline needs Undo and Discard while an edit is being rehearsed. */
    const unsaved = $derived(week.dirty);

    $effect(() => {
        void store.editStamp;
        if (store.loaded) { store.queueAutoSave(); }
    });

    const saved = $derived.by(() => {
        if (store.scheduleGenerating > 0) { return german ? 'Schedule wird erstellt…' : 'creating schedule…'; }
        if (store.canRevert && week.dirty) { return german ? 'automatisch gespeichert, Programmplan ungespeichert' : 'auto-saved, schedule not saved'; }
        if (week.dirty) { return german ? 'ungespeicherter Programmplan' : 'unsaved schedule changes'; }
        if (store.canRevert) { return german ? 'automatisch gespeichert' : 'auto-saved'; }
        if (!store.savedAt) { return ''; }
        const seconds = Math.round((Date.now() - store.savedAt.getTime()) / 1000);
        return seconds < 60
            ? (german ? 'gerade gespeichert' : 'saved a moment ago')
            : (german ? 'vor ' + Math.round(seconds / 60) + ' Min. gespeichert' : 'saved ' + Math.round(seconds / 60) + ' min ago');
    });

    /*
        The configuration first, then the schedule. That order matters when both are waiting:
        the week is stored against a channel the configuration has to already name, and a
        schedule saved against a channel that was never written down would be orphaned.
    */
    async function saveAll(): Promise<void> {
        if (store.dirty) { await store.save(); }
        const hadSchedule = week.dirty;
        await week.commit();
        // "saved a moment ago" is about the PAGE, not only the configuration. Without this a
        // save that was nothing but schedule work left the header blank, which reads as a
        // button that did nothing.
        if (hadSchedule && !week.dirty) { store.savedAt = new Date(); }
    }
</script>

<div class="app">
    {#if !connected}
        <p class="fatal">{new NoDashboardError().message}</p>
    {:else if store.error}
                    <p class="fatal">{german ? 'Die Konfiguration konnte nicht geladen werden: ' : 'The configuration could not be loaded: '}{store.error}</p>
    {:else if store.loading}
                <p class="waiting">{german ? 'Wird geladen…' : 'Loading…'}</p>
    {:else}
        <Rail bind:destination />

        <div class="main">
            {#if destination === 'suggest'}
                <!--
                    A channel that has just been made opens on its WEEK.

                    The tab is remembered across channels, which is right when you are moving
                    between channels doing the same thing to each - and wrong the moment a
                    channel is created, because it lands you on whichever screen you happened
                    to leave last. The owner made one from nothing and arrived at Look.
                -->
                <Suggest
                    onDone={() => { destination = 'channel'; tab = 'week'; }}
                    onBlank={() => { store.addChannel('New channel'); destination = 'channel'; tab = 'week'; }}
                />
            {:else if destination === 'server'}
                <Server />
            {:else if !channel}
                <!--
                    The header comes too, because deleting the last channel is a change that has
                    to be savable. Without it, Save lived only inside the branch that draws a
                    channel, and removing the final one hid the button that would have written
                    the removal down.
                -->
                <header>
                    <h1>{german ? 'Keine Kanäle' : 'No channels'}</h1>
                    <div class="spacer"></div>
                    <span class="saved" class:dirty={unsaved}>{saved}</span>
                    {#if store.canRevert}
                        <button type="button" class="save" onclick={() => void store.revert()}>{german ? 'Änderungen zurücksetzen' : 'Revert changes'}</button>
                    {/if}
                    {#if unsaved}<button type="button" class="save" onclick={saveAll}>{german ? 'Programmplan speichern' : 'Save schedule'}</button>{/if}
                </header>
                <p class="unported">
                    {store.dirty
                        ? (german ? 'Keine Kanäle übrig. Die Änderung wird automatisch gespeichert. Mit + kannst du einen neuen anlegen.' : 'Nothing is left. The change is saved automatically. Use + in the rail to make another.')
                        : (german ? 'Dieser Server hat noch keine Kanäle. Lege mit + einen an.' : 'This server has no channels yet. Use + in the rail to make one.')}
                </p>
            {:else}
                <header>
                    <h1>{channel.Name}</h1>
                    <div class="spacer"></div>
                    <span class="saved" class:dirty={unsaved}>{saved}</span>
                    {#if store.canRevert}
                        <button type="button" class="save" onclick={() => void store.revert()}>{german ? 'Änderungen zurücksetzen' : 'Revert changes'}</button>
                    {/if}
                    {#if unsaved}<button type="button" class="save" onclick={saveAll}>{german ? 'Programmplan speichern' : 'Save schedule'}</button>{/if}
                </header>

                <nav class="tabs">
                    {#each TABS as item (item.id)}
                        <button
                            type="button"
                            class="tab"
                            class:on={tab === item.id}
                            class:unbuilt={!item.ready}
                            aria-pressed={tab === item.id}
                            onclick={() => (tab = item.id)}
                        >{tabLabel(item.id)}</button>
                    {/each}
                </nav>

                {#if tab === 'week'}
                    <Week {channel} />
                {:else if tab === 'content'}
                    <Content />
                {:else if tab === 'breaks'}
                    <Breaks {channel} />
                {:else if tab === 'look'}
                    <Look {channel} />
                {:else if tab === 'settings'}
                    <Settings {channel} />
                {:else}
                    <p class="unported">
                        {TABS.find((t) => t.id === tab)?.label} has not been rebuilt yet.
                        It is still on the old configuration page.
                    </p>
                {/if}
            {/if}
        </div>
    {/if}
</div>

<style>
    .app {
        display: flex;
        /* Measured by main.ts from where the app actually sits; the fallback is only for a
           host that runs the app without it, such as the dev entry. */
        height: var(--lt-app-height, 82vh);
        min-height: 460px;
        overflow: hidden;
    }

    .main {
        flex-grow: 1;
        min-width: 0;
        display: flex;
        flex-direction: column;
        min-height: 0;
    }

    header {
        display: flex;
        align-items: center;
        gap: 13px;
        padding: 16px 22px 0;
    }

    h1 {
        font-size: 21px;
        font-weight: 700;
        color: var(--lt-text-strong);
        margin: 0;
    }

    .spacer { flex-grow: 1; }

    .saved { font-size: 12.5px; color: var(--lt-text-dim); }
    .saved.dirty { color: var(--lt-collection); }

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

    .save:disabled {
        background: none;
        border-color: var(--lt-line);
        color: var(--lt-text-faint);
        cursor: default;
    }

    .tabs {
        display: flex;
        gap: 9px;
        padding: 13px 22px;
        border-bottom: 1px solid var(--lt-line);
        flex-wrap: wrap;
        flex: 0 0 auto;
    }

    .tab {
        display: inline-flex;
        align-items: center;
        gap: 8px;
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

    .tab:hover:not(.on) { background: var(--lt-hover); }

    .tab.on {
        background: var(--lt-accent);
        border-color: var(--lt-accent);
        color: #fff;
        box-shadow: 0 4px 12px var(--lt-accent-glow);
    }

    .tab.unbuilt { opacity: 0.55; }

    .fatal, .waiting, .unported {
        padding: 20px 22px;
        margin: 0;
        font-size: 13.5px;
        max-width: 44rem;
    }

    .fatal { color: #e08585; }
    .waiting, .unported { color: var(--lt-text-dim); }
</style>
