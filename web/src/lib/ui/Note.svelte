<script lang="ts">
    import type { Snippet } from 'svelte';

    // The design's explanatory line: indented behind a short rule rather than boxed. It is used
    // often enough that it is worth being one thing.
    let { children }: { children: Snippet } = $props();
</script>

<p class="note">{@render children()}</p>

<style>
    .note {
        font-size: 12.5px;
        color: var(--lt-text-muted);
        padding-left: 14px;
        border-left: 2px solid var(--lt-line);
        margin: 0;
    }
</style>
