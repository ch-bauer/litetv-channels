<script lang="ts">
    /*
        Look: the three pictures a channel wears, the library's own artwork to pick from, and
        what it comes out looking like on the television.

        What the old page lost and the board keeps: a **Change** button per picture, a **preview
        of the television**, and **Borrow from a title** as its own card on the right rather than
        folded into the gallery.

        Five things the owner found wrong here, all fixed together:

         - the channel name was drawn over every picture, including the ones it has no business
           being on - only the television draws it, so only the television preview does;
         - there was no way to look for a picture that is not already in this channel's lineup;
         - the previews cropped, so the picture being judged was not the picture;
         - the upload button wore a glyph that is not an upload;
         - and the television preview was invented rather than drawn to the app's own rules.
           It now follows what the fork actually does: banner first in the channel list,
           backdrop first behind the channel screen, both heavily dimmed because channel
           artwork is whatever is on air and a banner is the brightest thing Jellyfin holds.
    */
    import Card from '../lib/ui/Card.svelte';
    import Note from '../lib/ui/Note.svelte';
    import SectionTitle from '../lib/ui/SectionTitle.svelte';
    import { absolute, api, authHeaders, dashboard, failureWords } from '../lib/jellyfin';
    import { search, type SearchHit } from '../lib/search';
    import type { TvChannel } from '../lib/types';

    let { channel }: { channel: TvChannel } = $props();

    type Slot = 'Banner' | 'Backdrop' | 'Poster';

    /*
        The three slots, each drawn at the shape it actually is.

        They used to be three boxes of roughly one size, which flatters none of them and lies
        about the banner: a banner is about five times as wide as it is tall, and in a box half
        as wide as it is tall there is nothing to judge. So the banner gets a row of its own,
        full width, and the backdrop and the poster share the row below - which is also the
        order of how much width each one needs.

        `aspect` is the frame's true ratio rather than a pixel height, so every picture is shown
        in the shape the television will put it in.
    */
    const SLOTS: { slot: Slot; name: string; ratio: string; aspect: string; wide: boolean }[] = [
        { slot: 'Banner', name: 'Banner', ratio: 'about 5:1', aspect: '1000 / 185', wide: true },
        { slot: 'Backdrop', name: 'Backdrop', ratio: '16:9', aspect: '16 / 9', wide: false },
        { slot: 'Poster', name: 'Poster', ratio: '2:3', aspect: '2 / 3', wide: false },
    ];

    const OVERLAY_SLOTS: { slot: Slot; name: string }[] = [
        { slot: 'Banner', name: 'Banner' },
        { slot: 'Poster', name: 'Poster' },
        { slot: 'Backdrop', name: 'Backdrop' },
    ];

    /*
        The gallery's filters, named after the three slots they fill.

        They used to be named after the SHAPE of the picture - "wide", "tall", "stills" - which
        is the wrong question. Nobody comes to this screen wanting a wide picture; they come
        wanting a banner, and then have to work out which shape a banner is made of. So each
        filter is named for the slot, and the shape it actually fetches is the hint underneath.

        The mapping is not quite one to one, and the hints say so rather than pretending. What
        a banner filter can offer depends on what the title IS, measured on the test server
        28 Aug 2026: **TheTVDB serves real banners for a series** - eight for SpongeBob - and
        **no provider serves one for a film**, where TheMovieDb has no banner at all. So the
        online search asks for a banner and falls back to the widest picture there is, saying
        which it ended up with.
    */
    type Shape = Slot;

    const SHAPES: { id: Shape; label: string; hint: string }[] = [
        { id: 'Banner', label: 'Banner', hint: 'true banners where the title has one - a series usually does, a film usually does not' },
        { id: 'Poster', label: 'Poster', hint: 'upright cover art' },
        { id: 'Backdrop', label: 'Backdrop', hint: 'full-frame 16:9 pictures' },
    ];

    let shape = $state<Shape>('Banner');

    /*
        Where the pictures come from. The owner asked whether the picture search uses the online
        one, and asked for offline first with online kept separate. It did not use it at all -
        every tile was a library picture, and there was nothing anywhere that asked a provider.

        So: `library` is what opens, and it is the whole gallery until somebody presses Online.
        Nothing reaches out on its own.
    */
    type Source = 'library' | 'online';

    let source = $state<Source>('library');

    /** The titles the gallery is showing pictures OF - this channel's, or a search's. */
    let ids = $state<string[]>([]);
    /*
        A tile carries whether it IS the kind that was asked for.

        `standIn` is the whole of item 1: a banner filter that falls back draws pictures that
        are not banners, and drawing them in a five-to-one cell crops away the very thing the
        owner is being asked to judge - "for that i have to see the picture". So the cell shape
        follows what is actually IN it, and the two kinds are drawn as two grids rather than
        mixed into one, because a grid with two cell shapes has ragged rows and that answer was
        already tried and rejected.
    */
    let onlineTiles = $state<Tile[]>([]);
    let onlineBusy = $state(false);
    let onlineNote = $state<string | null>(null);
    let loadingTiles = $state(false);
    let borrowTerm = $state('');
    let borrowHits = $state<SearchHit[]>([]);
    let uploading = $state(false);

    /*
        Looking for a picture beyond this channel's own lineup. The gallery could only ever
        offer the titles the channel already plays, which is no use to a channel built from a
        genre - and there was nothing to type into, which is what "image search does not work"
        was: there was no search.
    */
    let tileTerm = $state('');
    let tileSearching = $state(false);
    let searchedFrom = $state<string | null>(null);
    let tileTimer: ReturnType<typeof setTimeout> | undefined;

    /** Pictures the browser could not load, so a dead tile does not sit there as a grey box. */
    let dead = $state<Record<string, boolean>>({});

    const artwork = $derived(channel.Artwork as Record<string, string | null | undefined>);

    function setArtworkField(key: string, value: string | null): void {
        // Reassign the object instead of mutating a field on a derived reference. The explicit
        // assignment makes the first artwork change part of the config snapshot immediately;
        // otherwise the upload can finish before auto-save notices it and cleanup removes the
        // freshly uploaded file as unreferenced.
        channel.Artwork = { ...(channel.Artwork ?? {}), [key]: value };
        if (value) {
            const resolved = absolute(value);
            if (dead[resolved]) {
                dead = { ...dead, [resolved]: false };
            }
        }
    }

    function current(slot: Slot): string | null {
        const key = slot + 'Url';
        const value = artwork[key];
        if (typeof value !== 'string' || value.length === 0) { return null; }
        const resolved = absolute(value);
        return dead[resolved] ? null : resolved;
    }

    function artworkFailed(url: string): void {
        dead = { ...dead, [url]: true };
    }

    function imageFor(slot: Slot): string {
        return current(slot) ?? '';
    }

    function sourceOf(slot: Slot): string {
        if (current(slot)) { return 'set for this channel'; }
        const borrowed = artwork['ImageItemName'];
        if (typeof borrowed === 'string' && borrowed.length > 0) {
            return 'borrowed from ' + borrowed;
        }
        return 'whatever is on air';
    }

    /**
     * Which of Jellyfin's image kinds the LIBRARY is asked for.
     *
     * A banner asks for the thumb here: an item in the library usually has no banner of its own,
     * and the thumb is the widest thing it does have.
     */
    function kindOfShape(): string {
        return shape === 'Poster' ? 'Primary' : shape === 'Backdrop' ? 'Backdrop' : 'Thumb';
    }

    /**
     * Which kind the PROVIDERS are asked for, which is not the same question.
     *
     * They are asked for the thing itself - a banner for the banner, a poster for the poster, a
     * backdrop for the backdrop - because unlike a library item, a provider has a Banner type and
     * may well hold one. Asking for a thumb instead, which is what this used to do, meant the
     * banner section could never be offered an actual banner even where one existed.
     */
    function onlineKindOf(want: Shape): string {
        return want === 'Poster' ? 'Primary' : want === 'Backdrop' ? 'Backdrop' : 'Banner';
    }

    /*
        How tall a tile is drawn.

        It grows when there are few. A gallery of two pictures at the size a gallery of forty
        needs is two stamps in a field of nothing, and a picture is being JUDGED here - it has
        to be big enough to judge. The width follows in the grid's own minimum, so the two stay
        in proportion.

        The range is deliberately narrow - 1 to 1.8, where it used to reach 2.4. Switching from
        the library to the providers changes how many pictures there are, so a wide range meant
        every tile on screen changed size the moment that button was pressed, and the gallery
        appeared to heave. Big enough to judge, without the lurch.
    */
    function tileScale(count: number): number {
        if (count <= 0) { return 1; }
        if (count <= 2) { return 1.8; }
        if (count <= 4) { return 1.5; }
        if (count <= 8) { return 1.3; }
        if (count <= 14) { return 1.1; }
        return 1;
    }

    function tileHeight(count: number): number {
        const base = shape === 'Poster' ? 128 : shape === 'Banner' ? 84 : 62;
        return Math.round(base * tileScale(count));
    }

    /*
        Which library items hold a real banner, asked rather than assumed.

        The banner filter used to ask every item for its Thumb, so searching the library for a
        banner could not return one even when the item had one - and they do: this channel's own
        banner is a library item's Banner image. Jellyfin says which images an item holds in
        `ImageTags`, so one call settles it for the whole gallery, and each tile then asks for
        the banner where there is one and the widest thing there is where there is not.
    */
    let bannerKindById = $state<Record<string, string>>({});

    $effect(() => {
        const asked = ids;
        if (source !== 'library' || shape !== 'Banner' || asked.length === 0) {
            bannerKindById = {};
            return;
        }

        api().getItems<{ Items?: { Id: string; ImageTags?: Record<string, string> }[] }>(
            api().getCurrentUserId(),
            { ids: asked.join(','), fields: 'ImageTags' },
        )
            .then((answer) => {
                const map: Record<string, string> = {};
                for (const item of answer.Items ?? []) {
                    map[item.Id] = item.ImageTags?.Banner ? 'Banner' : 'Thumb';
                }
                bannerKindById = map;
            })
            // The gallery still works from the filter's own kind; this only sharpens it.
            .catch(() => { bannerKindById = {}; });
    });

    function tilesFor(list: string[]): Tile[] {
        const kind = kindOfShape();
        return list.map((id) => {
            // Only the banner filter has a stand-in: a backdrop and a poster are asked for by
            // name and the library either holds one or answers 404, which takes the tile off
            // the wall entirely.
            const asked = bannerKindById[id] ?? kind;
            return {
                url: absolute('/Items/' + id + '/Images/' + asked + '?maxHeight=480&quality=85'),
                height: tileHeight(list.length),
                standIn: shape === 'Banner' && asked !== 'Banner',
            };
        });
    }

    /*
        The gallery, derived rather than assembled: changing the shape or the source redraws it
        without re-running a search, which is what the shape filter used to do.
    */
    /*
        How many pictures are fetched at once.

        It used to take everything it could reach the moment the screen opened - two dozen
        library items, or six titles times eight pictures from the providers - which is a great
        many requests for a wall nobody has looked at yet. It asks for a screenful now, and for
        more only when asked.
    */
    const FIRST = 8;
    const MORE = 12;

    let wanted = $state(FIRST);

    /** Online needs whole titles, and a title is worth a few pictures. */
    const onlineTitles = $derived(Math.max(2, Math.ceil(wanted / 4)));

    const tiles = $derived(
        source === 'library'
            ? tilesFor(ids.slice(0, wanted))
            : onlineTiles.slice(0, wanted),
    );

    /*
        The gallery: one regular grid, in the shape of the slot being filled.

        Two earlier answers were both wrong. Tiles at their own widths wrapping freely left the
        right-hand edge ragged. Justifying the rows fixed the edge but set each row's height from
        what happened to land in it, so a full row came out small and a short one came out large -
        "first two rows not visible good, and then 2 big pictures".

        A grid has neither fault: every picture is the same size, the rows are flush, and nothing
        jumps. The cell takes the SHAPE of the slot, which is what keeps the cropping honest -
        the filter has already fetched pictures of that kind, so a poster lands in a poster-shaped
        cell and hardly loses anything. What it does lose is decided properly later, by Crop.
    */
    interface Tile { url: string; height: number; standIn: boolean }

    const CELL_SHAPE: Record<Slot, { aspect: string; perRow: number }> = {
        // One across. A banner is five to one, so two of them side by side are two slivers.
        Banner: { aspect: '1000 / 185', perRow: 1 },
        Backdrop: { aspect: '16 / 9', perRow: 3 },
        Poster: { aspect: '2 / 3', perRow: 4 },
    };

    const cells = $derived(CELL_SHAPE[shape]);

    /*
        The cell a STAND-IN goes in, which is the shape of the picture rather than of the slot.

        Only the banner filter ever falls back, and what it falls back to is a thumb - sixteen
        to nine, three across, exactly the backdrop cell. So this is not a fourth shape to keep
        in step with the other three; it is the one the pictures already are.
    */
    const standInCells = CELL_SHAPE.Backdrop;

    /*
        Two walls, not one mixed one. Sliced FIRST, so "show more" still means the same number
        of pictures however they divide.
    */
    const trueTiles = $derived(tiles.filter((t) => !t.standIn && !dead[t.url]));
    const standIns = $derived(tiles.filter((t) => t.standIn && !dead[t.url]));

    /**
     * Why the stand-ins are on the wall. Said once, above the pictures it is about, rather than
     * over the whole gallery - the library half never had this line at all and fell back
     * silently.
     */
    const standInNote = $derived(
        standIns.length === 0
            ? null
            : source === 'online'
                ? 'No provider had a true banner for these titles, so these are the widest '
                    + 'pictures they do have.'
                : 'These titles hold no banner, so this is the widest picture they do have.',
    );

    /** Whether there is anything left to fetch, which is what shows the button. */
    const moreToFetch = $derived(
        source === 'library'
            ? ids.length > wanted
            : onlineTiles.length > wanted || onlineTitles < ids.length,
    );

    // Back to a screenful whenever the question changes.
    $effect(() => {
        void shape;
        void source;
        void searchedFrom;
        wanted = FIRST;
    });


    /** The titles this channel plays. */
    async function loadTiles(): Promise<void> {
        loadingTiles = true;
        try {
            searchedFrom = null;
            // A YouTube source has no library item, so there is no picture to ask for.
            ids = channel.Sources.filter((s) => s.Type !== 'YouTube').map((s) => s.ItemId).slice(0, 48);
        } finally {
            loadingTiles = false;
        }
    }

    /** Titles from anywhere in the library, not only what this channel plays. */
    async function searchTiles(): Promise<void> {
        const asked = tileTerm.trim();
        if (asked.length === 0) { void loadTiles(); return; }
        tileSearching = true;
        try {
            const hits = await search(asked, 24);
            if (asked !== tileTerm.trim()) { return; }
            searchedFrom = asked;
            ids = hits.map((h) => h.id);
        } catch {
            ids = [];
        } finally {
            tileSearching = false;
        }
    }

    /*
        The online half, and it only ever runs because somebody pressed Online.

        Jellyfin's own providers answer per ITEM, so this asks about the titles the gallery is
        already showing - the channel's lineup, or whatever was searched for. Six of them, eight
        pictures each: enough to choose from, few enough that pressing Online is not a minute of
        waiting on somebody else's servers.
    */
    interface RemoteImage { Url?: string; Width?: number; Height?: number; }

    async function loadOnline(): Promise<void> {
        const asking = ids.slice(0, onlineTitles);
        const forShape = shape;
        const wanted = onlineKindOf(forShape);
        onlineBusy = true;
        onlineNote = null;
        onlineTiles = [];
        try {
            const ask = async (kind: string): Promise<string[]> => {
                const answers = await Promise.all(asking.map((id) =>
                    api().getJSON<{ Images?: RemoteImage[] }>(
                        api().getUrl('Items/' + id + '/RemoteImages', {
                            type: kind,
                            limit: 8,
                            includeAllLanguages: true,
                        }),
                    ).catch(() => ({ Images: [] as RemoteImage[] }))));

                const found: string[] = [];
                for (const answer of answers) {
                    for (const image of answer.Images ?? []) {
                        if (image.Url && !found.includes(image.Url)) { found.push(image.Url); }
                    }
                }
                return found;
            };

            let urls = await ask(wanted);

            /*
                Nothing back for a banner is ordinary rather than a fault - a film has none,
                because TheMovieDb has no banner type at all, while a series usually does from
                TheTVDB. Rather than an empty wall, fall back to the widest thing they do have,
                and say which of the two is on screen.
            */
            let fellBack = false;
            if (urls.length === 0 && wanted === 'Banner') {
                urls = await ask('Thumb');
                fellBack = urls.length > 0;
            }

            // Still the shape and the titles that were asked about? A slow provider must not
            // repaint a gallery somebody has moved on from.
            if (forShape !== shape) { return; }

            onlineTiles = urls.map((url) => ({ url, height: tileHeight(urls.length), standIn: fellBack }));
            if (urls.length === 0) {
                onlineNote = asking.length === 0
                    ? 'Nothing to look up - search for a title first.'
                    : 'No provider had a ' + wanted.toLowerCase() + ' picture for these titles.';
            } else if (fellBack) {
                onlineNote = 'No provider had a true banner for these titles, so these are the '
                    + 'widest pictures they do have - the television crops one to fit.';
            }
        } catch (err) {
            onlineNote = 'The providers could not be asked: '
                + (failureWords(err));
        } finally {
            onlineBusy = false;
        }
    }

    function onTileSearch(): void {
        clearTimeout(tileTimer);
        tileTimer = setTimeout(searchTiles, 250);
    }

    // The channel's own lineup, whenever the channel changes. A shape change no longer re-runs
    // anything: the tiles are derived, so they simply redraw.
    $effect(() => {
        void channel.Id;
        if (tileTerm.trim().length > 0) { void searchTiles(); } else { void loadTiles(); }
    });

    // Online is asked again when the shape or the titles change, when more is asked for,
    // and never otherwise.
    $effect(() => {
        void shape;
        void ids;
        void onlineTitles;
        if (source === 'online') { void loadOnline(); }
    });

    /*
        Which picture a click is choosing a slot for.

        The gallery used to be DEAD until a slot was armed: every tile was a disabled button, so
        twelve bright pictures sat there and clicking any of them did nothing at all. The only
        clue was a line of small grey text off to the right saying "Press Change on a picture
        first", and the owner's report was the plain consequence - the picture search does not
        work. A gallery that cannot be clicked should not look exactly like one that can.

        So a click always does something now. With a slot armed it fills that slot, as before.
        With none armed it asks which - here, on the picture - because "wide" fits both the
        banner and the backdrop and guessing between them would be wrong half the time.
    */
    let offering = $state<string | null>(null);

    /*
        Clicking anything else lets the picture go, and so does Escape.

        A selection that can only be cleared by pressing the very thing that made it, or by
        hunting for Cancel, is one that follows you around - the same complaint this page has
        already answered for the channel list and the content list. Bound on the document rather
        than on a wrapper, so nothing has to become clickable to carry it.
    */
    $effect(() => {
        const letGo = (event: MouseEvent) => {
            const el = event.target as HTMLElement | null;
            // A click on a tile or on the bar itself is the selection being used, not dropped.
            if (el && el.closest('.tile, .use-as')) { return; }
            offering = null;
        };
        const onKey = (event: KeyboardEvent) => {
            if (event.key === 'Escape') { offering = null; }
        };
        document.addEventListener('click', letGo);
        document.addEventListener('keydown', onKey);
        return () => {
            document.removeEventListener('click', letGo);
            document.removeEventListener('keydown', onKey);
        };
    });

    async function useTile(url: string, slot: Slot | null): Promise<void> {
        if (!slot) { return; }
        const bar = dashboard();
        uploading = true;
        bar.showLoadingMsg();
        try {
            // Library images are protected by Jellyfin and cannot be downloaded by the server
            // without the viewer's token. Fetch them in the dashboard and upload the bytes.
            // Remote provider images can still be copied server-side, which also avoids making
            // the browser depend on their CORS policy.
            if (source === 'library' || url.startsWith(api().serverAddress())) {
                const picture = await fetch(url, { headers: authHeaders() });
                if (!picture.ok) { throw new Error(picture.status + ' ' + picture.statusText); }
                const bytes = await picture.blob();
                const uploaded = await fetch(api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + slot), {
                    method: 'POST',
                    headers: authHeaders(),
                    body: bytes,
                });
                if (!uploaded.ok) { throw new Error(uploaded.status + ' ' + uploaded.statusText); }
            } else {
                await api().fetch({
                    url: api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + slot + '/Fetch'),
                    type: 'POST',
                    data: JSON.stringify({ url }),
                    contentType: 'application/json',
                    dataType: 'json',
                });
            }
            setArtworkField(slot + 'Url', '/LiteTv/Artwork/' + channel.Id + '/' + slot + '?t=' + Date.now());
            offering = null;
        } catch (err) {
            bar.alert('That picture could not be taken: ' + (failureWords(err)));
        } finally {
            uploading = false;
            bar.hideLoadingMsg();
        }
    }

    async function upload(slot: Slot, file: File): Promise<void> {
        const bar = dashboard();
        uploading = true;
        bar.showLoadingMsg();
        try {
            // Straight fetch, because an upload cannot go through ApiClient - which is the one
            // place the token has to be put on by hand.
            const answer = await fetch(api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + slot), {
                method: 'POST',
                headers: authHeaders(),
                body: file,
            });
            if (!answer.ok) { throw new Error(answer.status + ' ' + answer.statusText); }
            setArtworkField(slot + 'Url', '/LiteTv/Artwork/' + channel.Id + '/' + slot + '?t=' + Date.now());
        } catch (err) {
            bar.alert('That picture could not be uploaded: ' + (failureWords(err)));
        } finally {
            uploading = false;
            bar.hideLoadingMsg();
        }
    }

    /*
        Cropping a picture into a slot.

        A picture is almost never the shape of the slot it goes in - a poster is upright, a
        banner is five to one - and until now the only answer was "the television crops it",
        which means the television decides which part of the picture you get. This decides it
        here instead.

        Zooming out PAST the edges of the picture is allowed on purpose: it is the only way to
        get an upright picture into a wide slot without throwing most of it away. The space that
        opens up is filled with a blurred, blown-up copy of the picture itself - what a
        broadcaster does with wrong-shaped material, and far better than a black bar.

        Done in the browser: the picture is already here, a canvas does it in one pass, and what
        comes out goes through the same endpoint that has always accepted an uploaded picture.
    */
    const CROP_OUTPUT: Record<Slot, { w: number; h: number }> = {
        Banner: { w: 1000, h: 185 },
        Backdrop: { w: 1280, h: 720 },
        Poster: { w: 400, h: 600 },
    };

    let cropping = $state<Slot | null>(null);
    let cropImage = $state<HTMLImageElement | null>(null);
    let cropBusy = $state(false);
    let cropError = $state<string | null>(null);

    /** Scale of 1 means the picture is drawn exactly as wide as the frame. */
    let cropScale = $state(1);
    /** Where the picture's top-left corner sits, in frame pixels. */
    let cropX = $state(0);
    let cropY = $state(0);
    let cropFrameWidth = $state(0);

    let cropFrameHeight = $state(0);

    /** The picture's drawn size in frame pixels, from the scale and its own proportions. */
    const cropDrawn = $derived.by(() => {
        const image = cropImage;
        if (!image || cropFrameWidth === 0) { return { w: 0, h: 0 }; }
        const w = cropFrameWidth * cropScale;
        return { w, h: w * (image.naturalHeight / image.naturalWidth) };
    });

    async function openCrop(slot: Slot): Promise<void> {
        const src = current(slot);
        if (!src) { return; }
        cropping = slot;
        cropError = null;
        cropImage = null;

        const image = new Image();
        /*
            Said outright even though it is the same server as the dashboard: without it the
            canvas is tainted and cannot be read back, which is the one way this whole thing
            fails - and it fails at the very last step, after the work is done.
        */
        image.crossOrigin = 'anonymous';
        image.src = src;
        try {
            await image.decode();
        } catch {
            cropError = 'That picture could not be read for cropping.';
            return;
        }
        if (cropping !== slot) { return; }
        cropImage = image;
        fitCrop('cover');
    }

    /**
     * Fills the frame with the picture, or fits the whole picture inside it.
     *
     * Scale is the drawn width as a fraction of the frame's width, so the drawn height is
     * `frameWidth * scale / imageRatio` and the frame's own height is `frameWidth / frameRatio`.
     * Covering therefore needs `scale >= 1` (wide enough) and `scale >= imageRatio / frameRatio`
     * (tall enough); containing needs both the other way about.
     */
    function fitCrop(how: 'cover' | 'contain'): void {
        const image = cropImage;
        const slot = cropping;
        if (!image || !slot || cropFrameWidth === 0) { return; }
        const frameRatio = CROP_OUTPUT[slot].w / CROP_OUTPUT[slot].h;
        const imageRatio = image.naturalWidth / image.naturalHeight;
        const tallEnough = imageRatio / frameRatio;
        cropScale = how === 'cover' ? Math.max(1, tallEnough) : Math.min(1, tallEnough);
        centreCrop();
    }

    function centreCrop(): void {
        cropX = (cropFrameWidth - cropDrawn.w) / 2;
        cropY = (cropFrameHeight - cropDrawn.h) / 2;
    }

    function closeCrop(): void {
        cropping = null;
        cropImage = null;
        cropError = null;
    }

    /* Dragging the picture about inside the frame. */
    let dragFrom: { x: number; y: number; atX: number; atY: number } | null = null;

    function cropDown(event: PointerEvent): void {
        if (!cropImage) { return; }
        (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
        dragFrom = { x: event.clientX, y: event.clientY, atX: cropX, atY: cropY };
    }

    function cropMove(event: PointerEvent): void {
        if (!dragFrom) { return; }
        cropX = dragFrom.atX + (event.clientX - dragFrom.x);
        cropY = dragFrom.atY + (event.clientY - dragFrom.y);
    }

    function cropUp(): void {
        dragFrom = null;
    }

    /** Zoom about the middle of the frame, so the picture does not walk off as it grows. */
    function zoomTo(next: number): void {
        const was = cropScale;
        const to = Math.min(6, Math.max(0.1, next));
        const factor = to / was;
        cropX = cropFrameWidth / 2 - (cropFrameWidth / 2 - cropX) * factor;
        cropY = cropFrameHeight / 2 - (cropFrameHeight / 2 - cropY) * factor;
        cropScale = to;
    }

    function cropWheel(event: WheelEvent): void {
        if (!cropImage) { return; }
        event.preventDefault();
        zoomTo(cropScale * (event.deltaY < 0 ? 1.08 : 1 / 1.08));
    }

    async function applyCrop(): Promise<void> {
        const image = cropImage;
        const slot = cropping;
        if (!slot || !image || cropFrameWidth === 0) { return; }
        const out = CROP_OUTPUT[slot];
        cropBusy = true;
        cropError = null;
        try {
            const canvas = document.createElement('canvas');
            canvas.width = out.w;
            canvas.height = out.h;
            const ctx = canvas.getContext('2d');
            if (!ctx) { throw new Error('this browser gave no canvas to draw on'); }

            // Frame pixels to output pixels.
            const k = out.w / cropFrameWidth;

            /*
                The fill first: the picture blown up to cover the canvas and blurred hard. Drawn
                larger than the canvas because a blur drags the transparent outside inwards,
                which shows as a pale rim along the edges.
            */
            const cover = Math.max(out.w / image.naturalWidth, out.h / image.naturalHeight) * 1.3;
            const fillW = image.naturalWidth * cover;
            const fillH = image.naturalHeight * cover;
            ctx.filter = 'blur(' + Math.max(8, Math.round(out.w / 40)) + 'px)';
            ctx.drawImage(image, (out.w - fillW) / 2, (out.h - fillH) / 2, fillW, fillH);
            ctx.filter = 'none';

            // Then the picture itself, exactly where the frame was showing it.
            ctx.drawImage(image, cropX * k, cropY * k, cropDrawn.w * k, cropDrawn.h * k);

            const blob = await new Promise<Blob | null>((resolve) =>
                canvas.toBlob(resolve, 'image/jpeg', 0.92));
            if (!blob) { throw new Error('the cropped picture could not be encoded'); }

            const answer = await fetch(api().getUrl('LiteTv/Artwork/' + channel.Id + '/' + slot), {
                method: 'POST',
                headers: authHeaders(),
                body: blob,
            });
            if (!answer.ok) { throw new Error(answer.status + ' ' + answer.statusText); }

            setArtworkField(slot + 'Url', '/LiteTv/Artwork/' + channel.Id + '/' + slot + '?t=' + Date.now());
            closeCrop();
        } catch (err) {
            cropError = 'That crop could not be saved: ' + failureWords(err);
        } finally {
            cropBusy = false;
        }
    }

    function clearSlot(slot: Slot): void {
        setArtworkField(slot + 'Url', null);
    }

    async function findBorrow(): Promise<void> {
        // Guarded like every other search on the page: answers do not come back in the order
        // they were asked, and an old one landing last shows titles that do not match.
        const asked = borrowTerm;
        if (asked.trim().length === 0) { borrowHits = []; return; }
        try {
            const found = await search(asked, 8);
            if (asked !== borrowTerm) { return; }
            borrowHits = found;
        } catch {
            if (asked === borrowTerm) { borrowHits = []; }
        }
    }

    function borrow(hit: SearchHit): void {
        setArtworkField('ImageItemId', hit.id);
        setArtworkField('ImageItemName', hit.name);
        borrowTerm = '';
        borrowHits = [];
    }

    /*
        Which picture each screen ends up drawing - read off the app's `artFor`, not guessed.

        Its order per slot is: the picture chosen for THIS slot, then what the plugin found in
        the lineup for this slot, then what it found for the other wide slot, and only last the
        picture chosen for the other wide slot. Two things follow that this preview had wrong.

        **The poster is not in the wide chain at all.** `ArtKind.Card` is banner, found banner,
        found backdrop, chosen backdrop - a poster never reaches the list card, so offering it
        here as "the upright picture is stretched into the card" described something the
        television does not do.

        **Whatever is on air comes BEFORE the other chosen picture**, and that ordering is not an
        accident: the app's own note says consulting the other chosen picture earlier meant one
        custom banner silently took over the background as well. So when the picture below is the
        other slot's, it is the LAST resort and not the next one, and the words have to say so -
        what is actually on air cannot be drawn here.
    */
    const listPicture = $derived(current('Banner') ?? current('Backdrop'));
    const screenPicture = $derived(current('Backdrop') ?? current('Banner'));

    const listWords = $derived(
        current('Banner') ? 'Your banner.'
            : current('Backdrop') ? 'No banner set. The card takes a wide picture from what is on air first, and falls back to your backdrop only if it finds none.'
                : current('Poster') ? 'Only a poster is set, and the card never uses one - it wears whatever is on air.'
                    : 'Nothing set - the card wears whatever is on air.',
    );

    const screenWords = $derived(
        current('Backdrop') ? 'Your backdrop.'
            : current('Banner') ? 'No backdrop set. The screen takes a wide picture from what is on air first, and falls back to your banner only if it finds none.'
                : 'Nothing set - the screen wears whatever is on air.',
    );

    function stopBorrowing(): void {
        setArtworkField('ImageItemId', null);
        setArtworkField('ImageItemName', null);
    }
</script>

<div class="screen">
    <div class="left">
        <div>
            <SectionTitle>The channel’s three pictures</SectionTitle>
            <div class="spaced">
                <Note>
                    Set none and the channel wears whatever is on air — which is fine for one built
                    from a single series, and a black rectangle for one built from a genre.
                </Note>
            </div>
        </div>

        <div class="slots">
            {#each SLOTS as entry (entry.slot)}
                <div class="slot" class:full={entry.wide} class:cropping={cropping === entry.slot}>
                    <div class="slot-head">
                        <span class="slot-name">{entry.name}</span>
                        <span class="ratio">{entry.ratio}</span>
                    </div>

                    <!--
                        No channel name over it. The name belongs to the television, which draws
                        it once, over the artwork it chooses - drawing it on all three previews
                        put it where it will never appear and hid the picture being judged.
                    -->
                    {#if cropping === entry.slot}
                        <div
                            class="frame cropping"
                            class:capped={entry.slot === 'Poster'}
                            style="aspect-ratio: {entry.aspect}"
                            bind:clientWidth={cropFrameWidth}
                            bind:clientHeight={cropFrameHeight}
                            onpointerdown={cropDown}
                            onpointermove={cropMove}
                            onpointerup={cropUp}
                            onpointercancel={cropUp}
                            onwheel={cropWheel}
                            role="application"
                            aria-label="Drag to move the picture, scroll to zoom"
                        >
                            {#if cropImage}
                                <img class="crop-fill" src={cropImage.src} alt="" />
                                <img
                                    class="crop-live"
                                    src={cropImage.src}
                                    alt=""
                                    style="left: {cropX}px; top: {cropY}px; width: {cropDrawn.w}px; height: {cropDrawn.h}px"
                                />
                            {/if}
                        </div>
                    {:else}
                        <div
                            class="frame"
                            class:capped={entry.slot === 'Poster'}
                            style="aspect-ratio: {entry.aspect}"
                        >
                            {#if current(entry.slot)}
                                {@const image = imageFor(entry.slot)}
                                <img src={image} alt="" onerror={() => artworkFailed(image)} />
                            {:else}
                                <span class="frame-empty">nothing set</span>
                            {/if}
                        </div>
                    {/if}

                    {#if cropping === entry.slot}
                        <div class="crop-tools">
                            <input
                                type="range"
                                min="0.1"
                                max="6"
                                step="0.01"
                                value={cropScale}
                                oninput={(e) => zoomTo(Number(e.currentTarget.value))}
                                aria-label="Zoom"
                                disabled={!cropImage}
                            />
                            <button type="button" class="filter" title="Fill the frame" onclick={() => fitCrop('cover')} disabled={!cropImage}>Fill</button>
                            <button type="button" class="filter" title="Fit the whole picture in" onclick={() => fitCrop('contain')} disabled={!cropImage}>All</button>
                        </div>

                        {#if cropError}
                            <p class="crop-error">{cropError}</p>
                        {/if}

                        <div class="slot-actions">
                            <button type="button" class="change" onclick={applyCrop} disabled={!cropImage || cropBusy}>
                                <span class="lt-swap">
                                    <span class="lt-ghost">Use this crop</span>
                                    <span>{cropBusy ? 'Saving…' : 'Use this crop'}</span>
                                </span>
                            </button>
                            <button type="button" class="icon" title="Cancel" aria-label="Cancel cropping" onclick={closeCrop} disabled={cropBusy}>✕</button>
                        </div>
                    {:else}
                    <div class="source">{sourceOf(entry.slot)}</div>

                    <div class="slot-actions">
                        <!--
                            "Crop", not "Change". Choosing a different picture is what the
                            gallery below is for - clicking one there offers the three slots -
                            so this button was the second way to do the one thing, and no way at
                            all to do the thing people actually wanted.
                        -->
                        <button
                            type="button"
                            class="change"
                            disabled={!current(entry.slot)}
                            title={current(entry.slot)
                                ? 'Choose which part of this picture the ' + entry.name.toLowerCase() + ' shows'
                                : 'Nothing set yet - pick a picture below first'}
                            onclick={() => openCrop(entry.slot)}
                        >{cropping === entry.slot ? 'Cropping…' : 'Crop'}</button>

                        <!--
                            Icon only, at the owner's word. It is drawn a little larger than it
                            was: at thirteen pixels the tray-and-arrow was a smudge, which is
                            what "the upload symbol is weird" was about.
                        -->
                        <label class="icon" title="Upload a picture from this computer" aria-label="Upload a picture">
                            <input
                                type="file"
                                accept="image/*"
                                onchange={(e) => {
                                    const file = e.currentTarget.files?.[0];
                                    if (file) { void upload(entry.slot, file); }
                                    e.currentTarget.value = '';
                                }}
                            />
                            <!-- An arrow going up out of a tray, which is what uploading looks like. -->
                            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                <path d="M12 16V4" /><path d="m7 9 5-5 5 5" /><path d="M4 16v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" />
                            </svg>
                        </label>

                        {#if current(entry.slot)}
                            <button
                                type="button"
                                class="icon"
                                title="Use whatever is on air again"
                                aria-label="Clear the {entry.name.toLowerCase()}"
                                onclick={() => clearSlot(entry.slot)}
                            >✕</button>
                        {/if}
                    </div>
                    {/if}
                </div>
            {/each}
        </div>

        <div class="gallery">
            <div class="gallery-head">
                <!--
                    One heading, whichever source is on.

                    It used to name the source - "Pictures your library already has" against
                    "Pictures from the providers" - and the filters sit next to it, so pressing
                    Online slid them sideways, out from under the pointer that had just pressed
                    one. The row below says which source is on, in the control that sets it, so
                    the heading was saying it twice anyway.
                -->
                <h3>Pictures to choose from</h3>
                <div class="filters">
                    {#each SHAPES as option (option.id)}
                        <button
                            type="button"
                            class="filter"
                            class:on={shape === option.id}
                            title={option.hint}
                            onclick={() => (shape = option.id)}
                        >{option.label}</button>
                    {/each}
                </div>
                <div class="filters" role="group" aria-label="Where the pictures come from">
                    <button
                        type="button"
                        class="filter"
                        class:on={source === 'library'}
                        onclick={() => (source = 'library')}
                    >in your library</button>
                    <button
                        type="button"
                        class="filter"
                        class:on={source === 'online'}
                        onclick={() => { source = 'online'; void loadOnline(); }}
                        title="Asks Jellyfin's own picture providers about these titles"
                    >online</button>
                </div>

            </div>




            <div class="tile-search">
                <input
                    type="search"
                    bind:value={tileTerm}
                    oninput={onTileSearch}
                    placeholder="Look for pictures from any title..."
                    aria-label="Look for pictures from any title in the library"
                />
                {#if tileSearching}<span class="tile-note">looking...</span>{/if}
                {#if searchedFrom}
                    <span class="tile-note">
                        pictures from titles matching &ldquo;{searchedFrom}&rdquo;
                        <button type="button" class="link" onclick={() => { tileTerm = ''; void loadTiles(); }}>
                            back to this channel
                        </button>
                    </span>
                {/if}
            </div>

            <!--
                Two walls: the pictures that ARE what was asked for, then the ones standing in
                for them - and the stand-ins get cells of their own shape.

                One grid could not do this. A cell in the slot's shape crops a stand-in to five
                to one, so the owner is asked to choose and crop a picture they cannot see;
                letting each cell take its own shape puts the ragged rows back that the grid was
                introduced to fix. Two grids, each regular, is the only answer that is neither.
            -->
            {#if trueTiles.length > 0}
                <div
                    class="tiles"
                    style="--lt-cols: {cells.perRow}; --lt-cell: {cells.aspect}"
                >
                    {#each trueTiles as tile (tile.url)}
                        <button
                            type="button"
                            class="tile"
                            class:offered={offering === tile.url}
                            disabled={uploading}
                            title="Choose what to use this picture as"
                            onclick={() => (offering = offering === tile.url ? null : tile.url)}
                        >
                            <!--
                                A title with no picture of this shape answers 404, and the tile
                                was a grey box that could be chosen. It takes itself off the wall
                                instead.
                            -->
                            <img src={tile.url} alt="" loading="lazy" onerror={() => (dead[tile.url] = true)} />
                        </button>
                    {/each}
                </div>
            {/if}

            {#if standIns.length > 0}
                <p class="fell-back">{standInNote}</p>
                <div
                    class="tiles stand-ins"
                    style="--lt-cols: {standInCells.perRow}; --lt-cell: {standInCells.aspect}"
                >
                    {#each standIns as tile (tile.url)}
                        <button
                            type="button"
                            class="tile"
                            class:offered={offering === tile.url}
                            disabled={uploading}
                            title="Choose what to use this picture as"
                            onclick={() => (offering = offering === tile.url ? null : tile.url)}
                        >
                            <img src={tile.url} alt="" loading="lazy" onerror={() => (dead[tile.url] = true)} />
                        </button>
                    {/each}
                </div>
            {/if}

            {#if trueTiles.length === 0 && standIns.length === 0}
                <p class="none">
                    {#if loadingTiles || tileSearching || onlineBusy}
                        {onlineBusy ? 'Asking the providers...' : 'Looking...'}
                    {:else if source === 'online'}
                        {onlineNote ?? 'Nothing came back.'}
                    {:else if searchedFrom}
                        Nothing matching that has a {kindOfShape().toLowerCase()} picture.
                    {:else}
                        This channel has no content to take pictures from yet - search above for any title.
                    {/if}
                </p>
            {/if}

            {#if moreToFetch}
                <button
                    type="button"
                    class="filter more"
                    onclick={() => (wanted += MORE)}
                    disabled={onlineBusy || tileSearching}
                >Request more</button>
            {/if}

            {#if offering !== null}
                <!--
                    Asked rather than guessed. The shape filter narrows it but does not settle
                    it: a wide picture is a fair banner AND a fair backdrop, and putting it in
                    the wrong one is a worse answer than one more click.
                -->
                <div class="use-as">
                    <span>Use this picture as</span>
                    {#each OVERLAY_SLOTS as entry (entry.slot)}
                        <button type="button" onclick={() => useTile(offering!, entry.slot)}>
                            {entry.name}
                        </button>
                    {/each}
                    <button type="button" class="quiet" onclick={() => (offering = null)}>Cancel</button>
                </div>
            {/if}


        </div>
    </div>

    <div class="right">
        <Card>
            <h3>On the television</h3>

            <!--
                Two screens, because the app has two, and they choose differently: the channel
                list draws a row with the picture BESIDE the words at a banner's shape; the
                channel's own screen draws a full 16:9 frame behind them.

                Both are drawn to the app's own measurements rather than invented - a preview
                whose frame is the wrong shape shows a crop the television never makes, which is
                the one thing this card exists to get right.
            -->
            <div class="tv-label">In the channel list</div>
            <div class="tv">
                <!--
                    `ChannelRow`: a 78dp row on a dark ground, the words on the left and the
                    picture on the right at a Jellyfin banner's 1000:185. No wash over it - the
                    picture is beside the words, so nothing has to be read on top of it.
                -->
                <div class="tv-row">
                    <div class="tv-row-text">
                        <div class="tv-name">{channel.Name}</div>
                        <div class="tv-now">what is on now</div>
                        <div class="tv-bar"><span></span></div>
                        <div class="tv-next">Next&ensp;21:40&ensp;what follows it</div>
                    </div>
                    <div class="tv-row-art">
                        {#if listPicture}<img src={listPicture} alt="" />{/if}
                    </div>
                </div>
            </div>
            <p class="hint tight">{listWords}</p>

            <div class="tv-label">The channel&rsquo;s own screen</div>
            <div class="tv">
                <!--
                    A television frame, so 16:9. It used to be a fixed 120px band whatever the
                    column's width, which is nearer four to one - so the picture was cropped to a
                    shape the app never crops it to, and then judged on it.

                    The wash is the app's, in its two layers: an even 18% over everything, and a
                    left-to-right gradient from 72% to transparent by the right edge. That
                    gradient is most of what decides whether a picture works, because the right
                    of it is NOT dimmed - the guide sits over it - and a flat wash says otherwise.
                -->
                <div class="tv-hero">
                    {#if screenPicture}<img src={screenPicture} alt="" />{/if}
                    <div class="tv-dim"></div>
                    <div class="tv-dim-side"></div>
                    <div class="tv-screen">
                        <div class="tv-screen-left">
                            <div class="tv-head">
                                <div class="tv-cover">
                                    {#if current('Poster')}
                                        {@const image = imageFor('Poster')}
                                        <img src={image} alt="" onerror={() => artworkFailed(image)} />
                                    {/if}
                                </div>
                                <div class="tv-name">{channel.Name}</div>
                            </div>
                            <div class="tv-kicker">NOW</div>
                            <div class="tv-prog">what is on now</div>
                            <div class="tv-now">20:15 &ndash; 22:00 &middot; 40 min left</div>
                            <div class="tv-bar"><span></span></div>
                            <span class="tv-button">Watch live</span>
                        </div>
                        <div class="tv-screen-right">
                            <div class="tv-kicker">Coming up</div>
                            <div class="tv-slot"></div>
                            <div class="tv-slot"></div>
                            <div class="tv-slot"></div>
                        </div>
                    </div>
                </div>
            </div>
            <p class="hint tight">{screenWords}</p>

            <div class="tv-label">The cover on its own, uncropped</div>
            <div class="tv poster-strip">
                <div class="tv-poster">
                    {#if current('Poster')}
                        {@const image = imageFor('Poster')}
                        <img src={image} alt="" onerror={() => artworkFailed(image)} />
                    {/if}
                </div>
                <p class="hint tight">
                    {current('Poster')
                        ? 'The poster you set.'
                        : 'Nothing set - the app takes an upright picture from the lineup.'}
                </p>
            </div>

            <p class="hint">
                Cropped here exactly as the television crops: this is the one place a picture
                being cut off is the truth rather than a fault in the preview.
            </p>
        </Card>

        <Card>
            <h3>Borrow from a title instead</h3>
            <p class="hint tight">
                Name a film or series and the channel wears its artwork — and keeps following it,
                so re-scraping the series updates the channel too.
            </p>

            {#if artwork['ImageItemName']}
                <div class="borrowed">
                    <span>{artwork['ImageItemName']}</span>
                    <button type="button" onclick={stopBorrowing}>stop</button>
                </div>
            {/if}

            <input
                class="borrow"
                bind:value={borrowTerm}
                oninput={findBorrow}
                placeholder="Search a title…"
                aria-label="Borrow artwork from a title"
            />

            {#if borrowHits.length > 0}
                <div class="borrow-hits">
                    {#each borrowHits as hit (hit.id)}
                        <button type="button" onclick={() => borrow(hit)}>{hit.name}</button>
                    {/each}
                </div>
            {/if}
        </Card>

        <div class="footnote">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true">
                <circle cx="12" cy="12" r="9" /><path d="M12 8v5M12 16h.01" />
            </svg>
            A picture chosen from the web is downloaded and kept here, so it cannot stop working later.
        </div>
    </div>
</div>

<style>
    /*
        The whole screen has the ceiling, not the left half of it.

        Capping the left column alone kept the previews a sensible size but opened a band of
        nothing between the two halves, which is worse than either problem it solved. Capping
        here keeps the halves against each other and puts whatever a very wide window has spare
        outside them both, where there is nothing to look at anyway.
    */
    .screen {
        flex-grow: 1;
        min-height: 0;
        padding: 20px 22px;
        display: flex;
        gap: 16px;
        overflow: hidden;
        max-width: 1080px;
    }

    /*
        `padding-right` because this column scrolls: the banner now runs the full width of it, so
        without a gutter the picture ends hard against the scrollbar and reads as running under
        it. `scrollbar-gutter` keeps that gutter whether the bar is showing or not, so nothing
        shifts sideways when the content grows past the bottom.
    */
    .left {
        flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; gap: 16px;
        overflow-y: auto; padding-right: 8px; scrollbar-gutter: stable;
        /*
            One ceiling for everything in this column - the previews, the search and the
            pictures - so they line up down the left edge. Generous, because the screen itself is
            capped now; this only stops the pictures sprawling wider than the previews above
            them.
        */
        --lt-look-width: 620px;
    }
    /*
        Grows into the slack instead of leaving it between the two.

        The previews stop at their ceiling, so on a wide window the space between them and this
        panel was simply empty. Letting this side widen takes that space up - and it is the side
        that benefits, since it is drawing the television.
    */
    .right {
        flex: 1 1 330px;
        max-width: 460px;
        min-width: 300px;
        display: flex; flex-direction: column; gap: 15px; overflow-y: auto;
    }

    .spaced { margin-top: 6px; }

    /*
        Two rows: the banner across the top, the backdrop and the poster below it. A banner needs
        width more than anything else on this screen, and sharing a row three ways never gave it
        enough to be looked at.
    */
    /*
        `start`, so a card is only as tall as what is in it. Stretched to a common height the
        backdrop card carried a void the depth of the poster beside it - which is the same
        complaint as boxes of one size for pictures of three shapes, just moved.
    */
    /*
        The banner across the top; the backdrop and the poster below it.

        The poster's column is sized to the POSTER, not to half the row. Given half, the card was
        far wider than a two-by-three picture capped to fit it, and the picture sat in the middle
        with an empty band down each side - a gap inside the card. The card hugs it now, and the
        backdrop takes what is left.
    */
    .slots {
        display: grid; grid-template-columns: 1fr auto; gap: 14px; align-items: start;
        /*
            A ceiling, not a fixed width.

            These are previews, and a preview does not get better by being bigger: on a wide
            window the banner ran the whole column and came out enormous, growing every time the
            window did. Below the ceiling they still fill what there is, so a narrow window is
            unchanged; above it they simply stop.
        */
        max-width: var(--lt-look-width);
    }
    .slot.full { grid-column: 1 / -1; }

    .slot {
        min-width: 0;
        border: 1px solid rgba(255, 255, 255, .1);
        border-radius: var(--lt-radius);
        background: var(--lt-card);
        padding: 12px;
        display: flex;
        flex-direction: column;
        gap: 9px;
    }

    .slot.cropping { border-color: var(--lt-accent); }
    .tile.offered { box-shadow: inset 0 0 0 2px var(--lt-accent); }
    /*
        Sticks to the bottom of the column while a picture is chosen.

        The gallery is long and scrolls, so a bar at the end of it is nowhere near the picture
        just clicked - you would have to scroll to the bottom to say where it goes. Stuck to the
        view it travels with you, and the slot can be chosen from wherever in the results you
        are. Opaque, because the pictures run underneath it.
    */
    .use-as {
        position: sticky;
        bottom: 0;
        z-index: 2;
        display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
        margin: 10px 0 0; padding: 8px 10px;
        /*
            Solid, because it floats over the pictures.

            `--lt-card` on its own is a translucent tint - `rgba(255,255,255,.03)` - which is
            right for a card sitting ON the page and see-through for a bar sitting OVER it, and
            nothing above this in the tree has an opaque background to borrow: the colour comes
            from the dashboard's root, which paints a flat near-black. Laying that down would
            make the bar the one black thing on a screen of navy, so it uses the app's own dark
            blue - the far end of the gradient every picture frame is drawn on - with the usual
            card tint over it.
        */
        background: linear-gradient(var(--lt-card), var(--lt-card)) #151d2a;
        box-shadow: 0 -6px 12px -6px rgba(0, 0, 0, .6);
        border-left: 3px solid var(--lt-accent); border-radius: 4px;
    }
    .use-as span { color: var(--lt-text-dim); font-size: 12.5px; }
    .use-as button {
        background: var(--lt-accent); color: #fff; border: 0; border-radius: 4px;
        padding: 4px 10px; font-size: 12.5px; cursor: pointer;
    }
    .use-as button.quiet { background: transparent; color: var(--lt-text-dim); text-decoration: underline; }

    .slot-head { display: flex; align-items: baseline; gap: 7px; }
    .slot-name { font-size: 13px; font-weight: 700; color: var(--lt-text-title); }
    .ratio { font-size: 11px; color: var(--lt-text-dim); }

    /*
        The poster is the only one whose true shape makes it too big here: at the width of its
        card, two-by-three runs half again as tall as the backdrop beside it, and left the row
        with a hole under the shorter card. Capping its height and letting the width follow the
        ratio keeps it the right shape and brings the two back into line - which is better than
        narrowing the whole block, since that only moves the empty space to the right of it.
    */
    .frame.capped { height: 210px; width: auto; max-width: 100%; }

    .frame {
        width: 100%;
        border-radius: 5px;
        background: linear-gradient(140deg, #33455e, #151d2a);
        position: relative;
        overflow: hidden;
        display: flex;
        align-items: center;
        justify-content: center;
        padding: 0;
    }

    /*
        Fills the frame.

        It used to contain the whole picture, on the reasoning that this is where a picture is
        judged and a preview must not cut the sides off. That held while nobody could do anything
        about the shape - but it letterboxed every picture that was not exactly the slot's ratio
        against the frame's own background, which is the grey border the owner reported. Now that
        Crop decides which part is used, the frame shows what the television will show, and the
        way to change it is to press Crop.
    */
    .frame img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
    }

    .frame-empty {
        position: relative;
        margin: auto;
        font-size: 11px;
        color: rgba(255, 255, 255, .45);
    }

    .source { font-size: 11.5px; color: var(--lt-text-dim); }

    /*
        Pushed to the bottom of the card. The three frames are three different heights - a
        banner is 58px and a poster 128 - so everything under them used to sit at three
        different heights too, and the row read as unfinished rather than as three of a kind.
    */
    .slot-actions {
        display: flex; gap: 6px; margin-top: auto; align-items: center;
        /*
            Wraps, because the poster card is the narrowest of the three and three controls do
            not fit across it. Without this the last one is simply clipped off the edge - which
            is the "cut off" complaint made worse, not better.
        */
        flex-wrap: wrap;
    }

    .change {
        flex: 1 1 0;
        text-align: center;
        padding: 6px 0;
        border-radius: 5px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        font-size: 11.5px;
        font-weight: 600;
        font-family: inherit;
        color: var(--lt-text-body);
        cursor: pointer;
    }

    .icon {
        flex: 0 0 auto;
        padding: 6px 9px;
        border-radius: 5px;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        color: var(--lt-text-muted);
        cursor: pointer;
        font-size: 11px;
        font-family: inherit;
        display: inline-flex;
        align-items: center;
    }

    .icon input { display: none; }

    .gallery { max-width: var(--lt-look-width); }

    .gallery-head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }

    h3 { font-size: 13px; font-weight: 700; color: var(--lt-text-title); margin: 0; }

    .filters { display: flex; gap: 5px; }

    .filter {
        padding: 4px 11px;
        border-radius: 999px;
        font-size: 11.5px;
        font-weight: 600;
        font-family: inherit;
        background: none;
        border: 1px solid var(--lt-line-strong);
        color: var(--lt-text-dim);
        cursor: pointer;
    }

    .filter.on { background: var(--lt-accent); border-color: var(--lt-accent); color: #fff; }
    .more { margin-top: 10px; align-self: flex-start; }

    /*
        Item 63: the head jumped about whenever anything was selected. This line's text changes
        when a slot is picked - "Click a picture to use it" becomes "Pick one for the poster" -
        and in a wrapping flex row a text that changes width re-wraps the row and moves every
        control in it.

        It used to reserve fifteen ems against that, which held the row still but opened a
        canyon between the source buttons and this line. Taking the remaining space does the
        same job with no gap: the controls before it are laid out at their own sizes first, so
        nothing this line says can move them.
    */
    .tile-note {
        flex: 1 1 auto;
        min-width: 0;
        font-size: 12px;
        color: var(--lt-text-dim);
    }

    @media (max-width: 1000px) {
        .tile-note {
            flex: 1 0 100%;
            margin-left: 0;
            min-width: 0;
            text-align: left;
        }
    }

    /*
        The column width is set from how many pictures there are - see `tileScale`. `auto-fill`
        rather than `auto-fit` so two large pictures stay large instead of being stretched the
        width of the gallery between them.
    */
    /*
        A row of pictures at a common HEIGHT, each as wide as it happens to be.

        It used to be a grid of identical boxes with the picture contained inside one, which
        meant every picture whose shape did not match the box - most of them, since providers
        serve whatever they like - sat in a grey surround. Cropping them to fit would be worse:
        a gallery of cropped pictures is a gallery of the wrong pictures, and these are being
        judged. Sizing the tile to the picture instead is the third option, and it costs
        nothing: fix the height, let the width follow, and the browser does it from the image's
        own dimensions once it loads.

        The rows are ragged at the right edge, which is what a row of things of honest widths
        looks like.
    */
    .tiles {
        display: grid;
        grid-template-columns: repeat(var(--lt-cols, 3), 1fr);
        gap: 10px;
    }

    /* The second wall sits under the first; the note between them carries the gap above it. */
    .tiles.stand-ins { margin-top: 0; }

    /* The card's own frame, turned into the thing you drag. */
    .frame.cropping {
        cursor: grab;
        touch-action: none;
        user-select: none;
        outline: 2px solid var(--lt-accent);
        outline-offset: -2px;
    }

    .frame.cropping:active { cursor: grabbing; }

    /* The same fill the canvas paints: blown up, blurred, behind everything. */
    .crop-fill {
        position: absolute;
        inset: -8%;
        width: 116%;
        height: 116%;
        object-fit: cover;
        filter: blur(18px);
        pointer-events: none;
    }

    .crop-live { position: absolute; pointer-events: none; }


    .crop-tools { display: flex; align-items: center; gap: 6px; }
    .crop-tools input[type="range"] { flex: 1 1 60px; min-width: 50px; }
    .crop-error { margin: 0; font-size: 11.5px; color: var(--lt-bad, #e06c6c); }


    .fell-back {
        margin: 14px 0 8px;
        font-size: 11.5px;
        color: var(--lt-text-dim);
        border-left: 3px solid var(--lt-collection, var(--lt-accent));
        padding-left: 8px;
    }

    /*
        The tile is exactly the picture, and it gets there by CONSTRAINING rather than fixing.

        Fixing the height and letting the width run looks the same until a picture is wide enough
        for `max-width` to bite - a five-to-one banner at any useful height is wider than this
        column - and then the height is held, the width is clipped, and `contain` fills the
        difference with the background. That is the "large invisible border" the owner could see
        the moment a highlight was drawn around it: a tile far taller than the picture in it.

        A ceiling on each side instead. The picture is drawn at whichever limit it meets first,
        the button shrink-wraps it, and there is nothing left over to show.
    */
    .tile {
        border: none;
        padding: 0;
        border-radius: 5px;
        overflow: hidden;
        background: transparent;
        cursor: pointer;
        line-height: 0;
        aspect-ratio: var(--lt-cell, 16 / 9);
    }

    .tile:disabled { cursor: default; opacity: .75; }
    /*
        Inset shadow, not an outline. The tile is rounded and clips its overflow, which cut the
        corners off an outline drawn inside it and left the highlight looking broken; a shadow
        follows the radius exactly.
    */
    .tile:hover { box-shadow: inset 0 0 0 2px var(--lt-accent); }
    /*
        Fills the cell. The filter has already narrowed the pictures to the kind that fits it, so
        this takes a sliver off the edges at worst - and never leaves the background showing,
        which is what letterboxing them here used to do.
    */
    .tile img { display: block; width: 100%; height: 100%; object-fit: cover; }

    .tile-search { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }

    .tile-search input {
        flex: 1 1 200px;
        min-width: 0;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 7px 10px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .link {
        background: none;
        border: none;
        padding: 0;
        font: inherit;
        color: #9b8bf7;
        cursor: pointer;
        text-decoration: underline;
    }

    .tv { border-radius: var(--lt-radius-small); overflow: hidden; border: 1px solid var(--lt-line); margin-top: 6px; }

    .tv-label {
        margin-top: 13px;
        font-size: 10.5px;
        font-weight: 600;
        letter-spacing: .08em;
        text-transform: uppercase;
        color: var(--lt-text-dim);
    }

    /*
        THE TELEVISION FRAME IS 16:9, and that is not decoration.

        This was a fixed 120px band whatever the column's width - nearer four to one - so every
        picture was cropped to a shape the app never crops it to and then judged on that crop.
        The one promise this card makes is "cropped here exactly as the television crops"; a
        frame of the wrong shape breaks it before anything else can go wrong.
    */
    .tv-hero {
        background: linear-gradient(140deg, #33455e, #151d2a);
        position: relative;
        aspect-ratio: 16 / 9;
    }

    /*
        THE DIRECT CHILD, and the `>` is the whole of it.

        Written as `.tv-hero img` this caught the cover's picture as well - the cover lives
        inside the frame, beside the channel's name - and handed it `position: absolute; inset:
        0; width: 100%; height: 100%`. So the poster was stretched across the entire backdrop
        preview and painted over the words, and what the card showed under "your backdrop" was
        the poster. The owner spotted it in one look: "what is the giant poster crop doing there
        on the backdrop place".
    */
    .tv-hero > img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        object-fit: cover;
    }

    /*
        The app's wash, in its two layers (`ChannelBackdrop`): an even 18% over the whole frame,
        and a left-to-right gradient from 72% black to nothing by the right edge.

        The right of the picture is deliberately NOT dimmed - the guide is drawn over it - and a
        preview with one flat wash hides exactly that. Which half of a picture survives is most
        of what decides whether it works on this screen.
    */
    .tv-dim { position: absolute; inset: 0; background: rgba(0, 0, 0, .18); }

    .tv-dim-side {
        position: absolute;
        inset: 0;
        background: linear-gradient(90deg, rgba(0, 0, 0, .72) 0%, rgba(0, 0, 0, .25) 45%, rgba(0, 0, 0, 0) 100%);
    }

    /*
        The channel screen's own layout: what is on down the left, the guide down the right, in
        the app's own 1 : 1.15 split. The guide is three empty slots rather than invented
        programme names - this card is about the picture, and made-up titles would read as data.
    */
    .tv-screen {
        position: absolute;
        inset: 0;
        display: flex;
        gap: 4%;
        padding: 5.5%;
        color: #fff;
    }

    .tv-screen-left { flex: 1 1 0; display: flex; flex-direction: column; min-width: 0; }
    .tv-screen .tv-name { font-size: 13px; }
    .tv-screen .tv-now { font-size: 10.5px; margin-top: 2px; }
    .tv-screen .tv-bar { height: 3px; margin-top: 5px; }
    .tv-screen-right { flex: 1.15 1 0; min-width: 0; }

    .tv-head { display: flex; align-items: center; gap: 8px; }

    /* The cover the app draws beside the name, at its 2:3. */
    .tv-cover {
        flex: 0 0 auto;
        height: 34px;
        aspect-ratio: 2 / 3;
        border-radius: 3px;
        overflow: hidden;
        background: rgba(255, 255, 255, .12);
    }

    .tv-cover img { width: 100%; height: 100%; object-fit: cover; }

    .tv-kicker {
        margin-top: 8px;
        font-size: 9px;
        font-weight: 700;
        letter-spacing: .09em;
        text-transform: uppercase;
        color: #b9a8ff;
    }

    .tv-prog { font-size: 12.5px; font-weight: 600; margin-top: 1px; }

    .tv-slot {
        height: 15px;
        margin-top: 5px;
        border-radius: 3px;
        background: rgba(255, 255, 255, .14);
    }

    /*
        THE LIST ROW, to `ChannelRow`'s measurements: a dark 78dp row with the words on the left
        and the picture on the right at a banner's 1000:185, its right corners rounded off.

        It used to be drawn as a wide card with the name laid OVER the picture, which is a
        different screen from the one the app has - the name is never over the artwork there,
        and the artwork is never full-bleed. The picture keeps its true share of the row's width
        so the crop shown is the crop made.
    */
    /*
        The row's shape is the app's, and it has to be stated rather than left to the words.

        `align-items: stretch` let the text column decide the height, and the picture - which
        asked for 1000:185 - was stretched to whatever that came to: measured at 3.11:1, so the
        banner was shown at a crop the television never makes, in the preview whose one job is
        the crop. The row is given the app's own proportions instead (a 78dp row whose picture
        is 78 x 5.405 wide is 11.26 to one), the picture takes its true 48% of the width, and
        the height falls out of the two - which is what makes the crop exact.

        It is a small row at this column's width, and that is simply what a television row
        scaled to fit here IS. The words are scenery; the picture is the point.
    */
    /*
        The app's row, to scale - INCLUDING its words.

        The proportions came right before the type did. `aspect-ratio: 11.26` is the app's 78dp
        row with its banner beside it, and at this column's width that makes the preview about
        30px tall - into which four lines of 7.5-10.5px text and a bar did not fit. They ran over
        each other, which is what the owner saw as squished.

        So the row is a size container and every measurement inside it is a share of its height,
        taken from the app: a 78dp row, 8dp of padding above and below, 16dp in from the left,
        `titleMedium` (16sp) for the name, `bodyMedium` (14sp) for what is on, `bodySmall` (12sp)
        for what is next, a 4dp bar across 55%, and 1dp between the lines. 16 of 78 is 20.5% of
        the height, and that is what `20.5cqh` says. The preview is small because the row IS a
        sliver - the card exists to show the crop, and a picture judged at the wrong shape is the
        fault it was built to stop - but nothing in it overlaps at any width now.
    */
    .tv-row {
        display: flex;
        align-items: stretch;
        aspect-ratio: 11.26;
        background: #17171C;
        color: #fff;
        container-type: size;
    }

    .tv-row-text {
        flex: 1 1 auto;
        min-width: 0;
        display: flex;
        flex-direction: column;
        justify-content: center;
        /* 8dp above and below, 16dp in from the left, 14dp before the picture. */
        padding: 10.26cqh 17.95cqh 10.26cqh 20.51cqh;
        overflow: hidden;
        line-height: 1.25;
        /* Arrangement.spacedBy(1.dp). */
        gap: 1.28cqh;
    }

    /* titleMedium, bodyMedium, bodySmall - 16, 14 and 12sp of a 78dp row. */
    .tv-row .tv-name { font-size: 20.51cqh; }
    .tv-row .tv-now { font-size: 17.95cqh; margin-top: 0; }
    .tv-row .tv-next { font-size: 15.38cqh; }

    /* A 4dp bar, 3dp below what is on, across 55% - and the gap above "Next" is the app's
       `Spacer(Modifier.weight(1f))`, which takes whatever is left. */
    .tv-row .tv-bar { height: 5.13cqh; margin-top: 3.85cqh; width: 55%; }

    /* No aspect-ratio here: the row's own ratio and this width already give it 1000:185. */
    .tv-row-art {
        flex: 0 0 48%;
        background: linear-gradient(140deg, #33455e, #151d2a);
    }

    .tv-row-art img { width: 100%; height: 100%; object-fit: cover; display: block; }

    .tv-next {
        margin-top: auto;
        font-size: 7.5px;
        color: rgba(255, 255, 255, .75);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    /* The app draws a progress bar under what is on, in both places. */
    .tv-bar {
        height: 2px;
        width: 55%;
        margin-top: 3px;
        border-radius: 999px;
        background: rgba(255, 255, 255, .22);
        overflow: hidden;
    }

    .tv-bar span { display: block; height: 100%; width: 38%; background: var(--lt-accent); }

    .tv-name {
        font-size: 10.5px;
        font-weight: 700;
        color: #fff;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .tv-now {
        font-size: 8.5px;
        color: rgba(255, 255, 255, .72);
        margin-top: 1px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

    .tv-button {
        display: inline-block;
        align-self: flex-start;
        margin-top: 8px;
        padding: 3px 10px;
        border-radius: 999px;
        background: rgba(255, 255, 255, .92);
        color: #111827;
        font-size: 10.5px;
        font-weight: 700;
    }

    .poster-strip {
        display: flex;
        align-items: center;
        gap: 11px;
        padding: 9px;
        border: 1px solid var(--lt-line);
        background: rgba(0, 0, 0, .2);
    }

    .tv-poster {
        flex: 0 0 46px;
        height: 69px;
        border-radius: 4px;
        overflow: hidden;
        background: linear-gradient(140deg, #2b3d55, #151d2a);
    }

    .tv-poster img { width: 100%; height: 100%; object-fit: cover; }

    .hint { font-size: 12px; color: var(--lt-text-dim); margin: 9px 0 0; line-height: 1.5; }
    .hint.tight { margin: 6px 0 10px; color: var(--lt-text-muted); font-size: 12.5px; }

    .borrowed {
        display: flex;
        align-items: center;
        gap: 8px;
        font-size: 12.5px;
        color: var(--lt-text-title);
        margin-bottom: 8px;
    }

    .borrowed button {
        background: none;
        border: none;
        color: var(--lt-text-dim);
        font-family: inherit;
        font-size: 11.5px;
        cursor: pointer;
        text-decoration: underline;
    }

    .borrow {
        width: 100%;
        background: var(--lt-field);
        border: 1px solid var(--lt-line-strong);
        border-radius: var(--lt-radius-small);
        padding: 8px 11px;
        font-size: 13px;
        font-family: inherit;
        color: var(--lt-text);
    }

    .borrow-hits { margin-top: 7px; display: flex; flex-direction: column; }

    .borrow-hits button {
        text-align: left;
        background: none;
        border: none;
        border-bottom: 1px solid var(--lt-line-soft);
        padding: 7px 2px;
        font-size: 12.5px;
        font-family: inherit;
        color: var(--lt-text-muted);
        cursor: pointer;
    }

    .borrow-hits button:hover { color: var(--lt-text-title); }

    .footnote {
        display: flex;
        align-items: flex-start;
        gap: 9px;
        font-size: 12.5px;
        color: var(--lt-text-dim);
    }

    .footnote svg { flex: 0 0 auto; margin-top: 1px; }

    .none { grid-column: 1 / -1; margin: 0; font-size: 12.5px; color: var(--lt-text-dim); }
</style>
