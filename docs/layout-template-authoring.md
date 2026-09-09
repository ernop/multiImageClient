# Layout-template authoring

The web UI has one layout-template contract with two authoring paths:

1. draw a flat composition map in the existing editor;
2. import an ordinary image, choose 2–9 regions, and convert it locally into
   an editable flat map.

The imported source image is editor-only. It is decoded and segmented in the
browser, is never added to composer attachments, and is never posted to the
server. Only the resulting palette PNG is submitted.

## Image-derived maps

`Ui/wwwroot/layout-template-worker.js` receives a bounded pixel preview and
uses perceptual color, local texture, edge strength, and spatial position to
produce exactly the requested number of coarse regions. It returns region IDs,
not image pixels. The main UI renders those IDs with the fixed nine-color
palette using nearest-neighbor scaling, then installs that flat result as the
editor's base action. All existing paint, fill, shape, selection, undo, and
action-list tools remain available for correction.

The algorithm proposes geometry only. It never names a region or infers that a
photographed object is water, a road, a person, or anything else. Users supply
all region meanings.

## Submission contract

New jobs submit version-2 `layoutTemplate` metadata:

- exact `inputIndex`;
- `aspect`;
- `sourceKind`: `drawn` or `image-derived`;
- `regionCount`;
- nine index-aligned meaning strings.

Every nonblank meaning identifies one exact palette color, and its count must
equal `regionCount`. Image-derived maps require 2–9 regions; drawn maps require
1–9. A layout-template job contains exactly one input image: the flattened
map. Other attachments are rejected rather than silently assigned an
ambiguous provider role.

The editor quantizes every exported map—including drawn maps that used soft or
antialiased tools—to opaque white or one exact palette color. The server
independently verifies dimensions, opacity, declared palette identity, and a
minimum nontrivial area for every declared region. These rules apply to every
layout template regardless of the client-supplied `sourceKind`; that field is
display/restore metadata and never controls validation strictness. Photographic
pixels, textures, gradients, transparency-hidden source content, and one-pixel
declaration markers therefore cannot cross the provider boundary.

## Provider instructions

`UiJobRunner.ApplyLayoutTemplateInstruction` removes editable/stale layout
paragraphs from each private prompt copy and rebuilds the wire instruction
from structured metadata. The shared contract transfers only approximate
region position, adjacency, and coverage while prohibiting preservation of
palette, edges, outlines, silhouettes, perspective, texture, lighting, or
recognizable source structures.

Small endpoint-specific clauses account for transport semantics:

- gpt-image-2 and Grok edit transports are told not to edit, trace, or morph
  the map;
- FLUX.2 conditioning is restricted to coarse placement and coverage;
- Nano Banana 2 receives a layout-only reference preamble instead of the
  ordinary style/subject/composition reference wording.

Targets outside `UiJobRunner.SketchCapableKeys` remain selectable only through
the existing explicit amber warning path; no unsupported capability is
claimed.

## Restore behavior

The persisted accepted event carries the exact version-2 identity. Set-active
fetches and decodes all job inputs, validates the recorded map index and
dimensions, and swaps attachments plus editor state atomically. A restored
image-derived map is editable as one flattened base action. Its original
source photograph cannot be restored because it was intentionally never
submitted or persisted.
