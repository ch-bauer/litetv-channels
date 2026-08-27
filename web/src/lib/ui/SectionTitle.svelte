<script lang="ts">
    import type { Snippet } from 'svelte';

    let { aside, children }: { aside?: string; children: Snippet } = $props();
</script>

<div class="head">
    <h2>{@render children()}</h2>
    {#if aside}<span>{aside}</span>{/if}
</div>

<style>
    .head {
        display: flex;
        align-items: baseline;
        gap: 10px;
    }

    h2 {
        font-size: 15px;
        font-weight: 700;
        color: var(--lt-text-strong);
        margin: 0;
    }

    span {
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }
</style>
