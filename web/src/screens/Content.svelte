<script lang="ts">
    import Card from '../lib/ui/Card.svelte';
    import Note from '../lib/ui/Note.svelte';
    import SectionTitle from '../lib/ui/SectionTitle.svelte';
    import SourceList from '../lib/content/SourceList.svelte';
    import SourceSearch from '../lib/content/SourceSearch.svelte';
    import BlockGrid from '../lib/content/BlockGrid.svelte';
    import Layout from '../lib/content/Layout.svelte';
    import QueuePreview from '../lib/content/QueuePreview.svelte';
    import { store } from '../lib/config.svelte';

    const channel = $derived(store.channel);

    const poolSummary = $derived.by(() => {
        const sources = channel?.Sources ?? [];
        if (sources.length === 0) { return 'nothing yet'; }
        const films = sources.filter((s) => s.Type === 'Movie').length;
        const series = sources.filter((s) => s.Type === 'Series').length;
        const collections = sources.filter((s) => s.Type === 'Collection').length;
        return [
            films ? films + (films === 1 ? ' film' : ' films') : '',
            series ? series + (series === 1 ? ' series' : ' series') : '',
            collections ? collections + (collections === 1 ? ' collection' : ' collections') : '',
        ].filter(Boolean).join(' · ');
    });
</script>

{#if channel}
    <div class="content">
        <div class="left">
            <SectionTitle aside={poolSummary}>What this channel plays</SectionTitle>
            <Note>
                A series is every episode of it, a collection is everything in it.
                Order here is the order they are laid out in.
            </Note>

            <Card flush>
                <SourceList sources={channel.Sources} />
                <!--
                    Below the list, as the board has it. It was above in the old page, which put
                    the thing you use once between the title and the thing you read every time.
                -->
                <SourceSearch sources={channel.Sources} />
            </Card>

            <SectionTitle>Parts of the week that play something else</SectionTitle>
            <Note>
                The kids’ hour until noon, the film on Saturday.
                Whatever no block covers plays the list above.
            </Note>

            <BlockGrid {channel} />
        </div>

        <div class="right">
            <Layout {channel} />
            <QueuePreview {channel} />

            <div class="footnote">
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true">
                    <circle cx="12" cy="12" r="9" /><path d="M12 8v5M12 16h.01" />
                </svg>
                Changing any of this leaves the stored week alone.
                It applies when you lay the week out again.
            </div>
        </div>
    </div>
{:else}
    <p class="empty">This server has no channels yet.</p>
{/if}

<style>
    .content {
        flex-grow: 1;
        min-height: 0;
        padding: 20px 22px;
        display: flex;
        gap: 26px;
        overflow: hidden;
    }

    .left {
        flex: 1 1 0;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 14px;
        overflow-y: auto;
    }

    /*
        The board draws this at 330px. It holds a fixed set of controls that do not benefit from
        being wider, and letting it stretch is what turned the old page's right-hand column into
        a field of empty space at large sizes.
    */
    .right {
        flex: 0 0 330px;
        display: flex;
        flex-direction: column;
        gap: 15px;
        overflow-y: auto;
    }

    .footnote {
        display: flex;
        align-items: flex-start;
        gap: 9px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .footnote svg { flex: 0 0 auto; margin-top: 1px; }

    .empty { padding: 20px 22px; color: var(--lt-text-dim); }

    @media (max-width: 1100px) {
        /* The two columns stop being two below the width the board was drawn at. */
        .content { flex-direction: column; overflow-y: auto; }
        .right { flex: 0 0 auto; }
    }
</style>
