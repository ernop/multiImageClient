# Image download pipeline

Settled 2026-08-31. This is the product contract. There is no older
parallel system. There is no later size-variant plan.

## Goal

Show the selected image’s own pixels as soon as a card thumb exists.
Download each original at most once. Give the selected original the
network until its bytes arrive. Never put original bytes on a card.

## Two files per raster result

1. **Original.** Provider bytes, saved verbatim. Viewer, save-as,
   set-active, and video source use this URL.
2. **Card thumb.** Longest edge 640 px. JPEG quality 80, or PNG when
   the source has alpha. Stored on disk under the job’s `thumbs/`
   folder. Served only from the app origin as
   `/api/jobs/{id}/images/{gen}/{n}?thumb=1`.

There is no third size. Backblaze B2 does not resize. Extra saved
widths are not part of this product.

## Card rule

A card `<img>` src is only a card thumb.

`imageCardThumbUrl` is the only constructor:

- Use the recorded local thumb when the event has one.
- Else, if the original is same-origin, append `?thumb=1`.
- Else, set no src. Never append `?thumb=1` to a B2 URL. Never use
  the original as a card image.

New `gen-result` events always include index-aligned `thumbs[]`.

## Viewer rule

On open and on every step:

1. Drop the previous item's pixels. Do not keep them while the next
   file decodes (`img.src` would otherwise hold the old frame).
2. Paint that item's card thumb and chrome at once, marked **preview /
   not full resolution**.
3. If this item has no decoded thumb yet, fill the stage with a wordless
   pulsing surface. Chrome (place, generator, prompt) already names the
   new selection. Fetch that item's original through the one loader.
4. Swap to the decoded original when the selection is still current.
5. Clear the not-final mark and the pulsing fill.

Wheel and keys change the selection without waiting on the original.
The 80 px wheel threshold still filters trackpad noise. A wheel over a
viewer chrome box that already has a vertical scrollbar (prompt,
describe, guidance, side-status column, help panel) scrolls that box
only. It does not change the selected image. Ctrl+wheel (and Cmd+wheel)
jumps to the first image of the adjacent prompt, the same destination as
Ctrl+Left / Ctrl+Right. Wheel down matches Ctrl+Right (older prompt).
Wheel up matches Ctrl+Left (newer prompt).

## One original loader

`loadImageViewerEntry` is the only GET of an original in the composer's
viewer (`app.js`).

The goal-loop page uses the standalone module `Ui/wwwroot/viewer.js`
(2026-09-04). It applies the same rules with its own loader: card-thumb
preview first, atomic chrome, selected original holds the network,
±10 preload over 6 slots, late fetches guarded by selection identity.
The two implementations are the known duplication; the intended end
state is `app.js` on `viewer.js`. See `docs/goal-loop-prd.md`.

Hover, the viewer window, and set-active reuse that cache. The same
URL never has two in-flight GETs. Hover does not use a second
`fetch()`. Closing the viewer aborts unfinished GETs and keeps up to
24 decoded originals for the next open.

The selected original holds the network until `fetched` (bytes in).
Decode does not use the pipe. Neighbor originals start after that.
Concurrency is 6.

## What is not a download of the original

- Card thumbs
- The viewer’s first paint of those thumbs
- In-process preview strips (their own local thumbs)
- Failure-kept last partials (their own local thumbs)

Those are small same-origin files. They must not compete with the
selected original.

## Identity

A late fetch cannot paint over a newer selection. Save-as, set-active,
and video source always use the original, never the card thumb. The
dimensions slot shows exact `width×height` only after the original
paints.
