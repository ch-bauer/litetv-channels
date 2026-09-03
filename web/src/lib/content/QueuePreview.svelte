<script lang="ts">
    /*
        "The first few, as they would fall".

        The point of this card is that "shuffled" and "two at a time" stop being abstractions -
        so it has to answer to the controls above it *before* anything is saved, which is why the
        dealing is done in the page. See lib/deal.ts.
    */
    import { failureWords } from '../jellyfin';
    import { store } from '../config.svelte';
    import Card from '../ui/Card.svelte';
    import { deal, type DealtItem } from '../deal';
    import type { TvChannel } from '../types';

    let { channel }: { channel: TvChannel } = $props();

    let queue = $state<DealtItem[]>([]);
    let busy = $state(false);
    let failed = $state<string | null>(null);
    const german = $derived(store.config?.PageLanguage === 'de'
        || (store.config?.PageLanguage === 'auto' && typeof navigator !== 'undefined' && navigator.language.toLowerCase().startsWith('de')));

    // Everything the dealing depends on, named so the effect re-runs when any of it changes -
    // including the sources' own order, which is the whole point of being able to drag them.
    const signature = $derived([
        channel.Id,
        channel.Order,
        channel.EpisodesPerBlock,
        channel.Sources.map((s) => [s.Type, s.ItemId, s.Url, s.Name, s.Probability ?? 100].join(':')).join(','),
    ].join('|'));

    $effect(() => {
        const asked = signature;
        busy = true;
        failed = null;
        deal(channel.Sources, channel.Order, channel.EpisodesPerBlock, channel.Id)
            .then((dealt) => {
                // A slow answer must not overwrite a newer question.
                if (asked !== signature) { return; }
                queue = dealt;
            })
            .catch((err: unknown) => {
                failed = failureWords(err);
            })
            .finally(() => {
                if (asked === signature) { busy = false; }
            });
    });
</script>

<Card>
    <h3>{german ? 'Die ersten Titel im Zeitplan' : 'The first few in the schedule'}</h3>

    {#if failed}
        <p class="bad">{german ? 'Die Vorschau konnte nicht erstellt werden: ' : 'The queue could not be worked out: '}{failed}</p>
    {:else if queue.length === 0}
        <p class="none">{busy ? (german ? 'Wird berechnet…' : 'Working it out…') : (german ? 'Noch nichts zum Einplanen.' : 'Nothing to lay out yet.')}</p>
    {:else}
        <div class="queue" class:stale={busy}>
            {#each queue as item, index (item.id + ':' + index)}
                <div class="line">
                    <span class="n">{index + 1}</span>
                    <span class="bar"></span>
                    <span class="label" title={item.label}>{item.label}</span>
                </div>
            {/each}
        </div>
    {/if}
</Card>

<style>
    h3 {
        font-size: 13px;
        font-weight: 700;
        color: var(--lt-text-title);
        margin: 0 0 9px;
    }

    .queue {
        display: flex;
        flex-direction: column;
        gap: 6px;
        transition: opacity 120ms;
    }

    /* Dimmed rather than emptied while it recomputes: a list that blinks out on every keystroke
       reads as broken, and the previous answer is still the better thing to show. */
    .stale { opacity: 0.45; }

    .line { display: flex; align-items: center; gap: 9px; }

    .n {
        flex: 0 0 18px;
        font-size: 11px;
        color: var(--lt-text-faint);
    }

    .bar {
        flex: 0 0 3px;
        align-self: stretch;
        min-height: 1.1em;
        border-radius: 2px;
        background: var(--lt-queue);
    }

    .label {
        flex-grow: 1;
        min-width: 0;
        font-size: 12.5px;
        color: rgba(255, 255, 255, 0.75);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .none, .bad { margin: 0; font-size: 12.5px; }
    .none { color: var(--lt-text-dim); }
    .bad { color: #e08585; }
</style>
