import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

/*
 * The bundle is served by the plugin itself, from `LiteTv/Web/` - see WebController.cs. Two
 * things about the output matter and are not defaults:
 *
 *  - `base` has to be the serving path, or the entry's imports resolve against the dashboard's
 *    root and 404.
 *  - the output is FLAT. Every file is embedded in the assembly under a logical name built from
 *    its filename alone, so a nested `chunks/` directory would need path separators carried
 *    through MSBuild metadata and back out again in the controller. Flat costs nothing and
 *    removes that whole class of problem.
 *
 * The entry keeps a stable name because `configPage.html` names it in a script tag; everything
 * else is content-hashed so it can be cached hard. This is the shape Segment Editor uses, and
 * it is the reason their bundle is safe to embed.
 */
export default defineConfig({
    plugins: [svelte()],
    base: '/LiteTv/Web/',
    server: {
        port: 8123,
        strictPort: true,
        // The app is looked at inside a real Jellyfin dashboard on another origin - that is the
        // only place emby's chrome, the theme and the real data exist together. So the dev
        // server has to be readable from there.
        cors: true,
        origin: 'http://127.0.0.1:8123',
    },
    build: {
        outDir: 'dist',
        emptyOutDir: true,
        sourcemap: true,
        rollupOptions: {
            output: {
                entryFileNames: 'litetv.js',
                chunkFileNames: '[name].[hash].js',
                // The stylesheet keeps a stable name for the same reason the entry does:
                // `configPage.html` names both in static tags and cannot know a hash. Everything
                // else - fonts, pictures - stays hashed and is cached hard.
                assetFileNames: (info) =>
                    info.names?.some((n) => n.endsWith('.css'))
                        ? 'litetv.css'
                        : '[name].[hash][extname]',
            },
        },
    },
});
