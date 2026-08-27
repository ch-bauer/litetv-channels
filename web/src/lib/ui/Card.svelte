<script lang="ts">
    import type { Snippet } from 'svelte';

    // The board's card: a hairline, a barely-there fill, and 8px corners. `flush` is for the
    // ones that hold a list to their own edges, where padding would break the rows' rhythm.
    let { flush = false, children }: { flush?: boolean; children: Snippet } = $props();
</script>

<div class="card" class:flush>{@render children()}</div>

<style>
    .card {
        border: 1px solid var(--lt-line);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        padding: 15px 16px;
    }

    .flush {
        padding: 0;
        overflow: hidden;
    }
</style>
