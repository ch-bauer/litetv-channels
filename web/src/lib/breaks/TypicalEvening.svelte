<script lang="ts">
    /*
        A typical evening with the break settings as they stand - four programmes and whatever
        falls between them. It moved here with the settings it illustrates; a picture of breaks
        on a screen with no break settings on it explained nothing.
    */
    import Card from '../ui/Card.svelte';
    import type { TvChannel } from '../types';

    let { channel }: { channel: TvChannel } = $props();

    const preview = $derived.by(() => {
        const rows: { clock: string; label: string; fill: string }[] = [];
        let minutes = 20 * 60 + 15;
        const clock = (m: number) =>
            String(Math.floor(m / 60) % 24).padStart(2, '0') + ':' + String(m % 60).padStart(2, '0');

        const names = channel.Sources.length > 0
            ? channel.Sources.map((s) => s.Name)
            : ['Something from this channel'];
        const every = channel.TrailerEveryPrograms;

        for (let i = 0; i < 4; i++) {
            rows.push({ clock: clock(minutes), label: names[i % names.length], fill: '#5b6ee1' });
            minutes += 105;
            if (every > 0 && (i + 1) % every === 0) {
                rows.push({ clock: clock(minutes), label: 'Break — adverts, then a trailer', fill: '#d99a3a' });
                minutes += 5;
            }
        }
        return rows;
    });
</script>

<Card>
    <h3>What this adds up to</h3>
    <p class="sub">A typical evening with the settings as they stand.</p>
    <div class="preview">
        {#each preview as row, index (index)}
            <div class="prow">
                <span class="at">{row.clock}</span>
                <span class="bar" style="background: {row.fill}"></span>
                <span class="what" title={row.label}>{row.label}</span>
            </div>
        {/each}
    </div>
</Card>

<style>
    h3 { margin: 0 0 3px; font-size: 14px; font-weight: 600; color: var(--lt-text-title); }

    .sub { margin: 0 0 11px; font-size: 12px; color: var(--lt-text-dim); }

    .preview { display: flex; flex-direction: column; gap: 7px; }

    .prow { display: flex; align-items: center; gap: 9px; font-size: 12.5px; }

    .at { flex: 0 0 42px; color: var(--lt-text-dim); font-variant-numeric: tabular-nums; }

    .bar { flex: 0 0 4px; height: 15px; border-radius: 2px; }

    .what {
        flex-grow: 1;
        min-width: 0;
        color: var(--lt-text-muted);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }
</style>
