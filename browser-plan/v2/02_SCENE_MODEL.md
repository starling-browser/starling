# v2 — Scene model

> **Scope (In):** The `Starling.Scene` project. The types that are the v2 core.
> **Scope (Out):** The GPU seam (see [03_GPU_ABSTRACTION.md](03_GPU_ABSTRACTION.md)) and
> the renderer that draws a scene (Phase 2).
> **Code:** `src/v2/Starling.Scene/`. Tests: `tests/v2/Starling.Scene.Tests/`.

This doc is the spec for code that already exists. Read it next to the source.

---

## A. The shape

```
SurfaceGraph                       one frame's worth of surfaces
  └─ SurfaceLayer (one per producer)
       ├─ kind        Document | GeneratedUi | Video | Overlay | ExternalGuest
       ├─ transform, opacity, clip   compositing parameters, read per frame
       ├─ provenance  who made it, what it may do
       ├─ hit regions decoupled from paint
       └─ RenderScene
            ├─ RenderCommandBuffer   the draw commands, in paint order
            └─ RenderResourceTable   images, glyph runs, fonts, by stable id
```

The key split is content versus compositing parameters. A layer's `RenderScene` is its
content. Its transform, opacity, and clip are read every frame and applied at composite
time. A move or a fade does not rebuild the scene. This is the same idea the v1
layer-tree work proved, lifted into the core model.

## B. SurfaceGraph and SurfaceLayer

`SurfaceGraph` holds the surface size in device-independent pixels, the scale (the
device pixel ratio), and an ordered list of layers from bottom to top. The compositor
consumes one graph per frame.

`SurfaceLayer` carries its kind, its `RenderScene`, its compositing parameters, an
optional `ProvenanceTag`, and a `HitRegionSet`. Every producer in v2 makes a layer. A
web page, a generated panel, a video, an overlay, and a guest surface all look the same
to the compositor.

## C. RenderScene, RenderCommandBuffer, RenderCommand

`RenderScene` is a `RenderCommandBuffer` plus a `RenderResourceTable`.

`RenderCommandBuffer` is an append-only list of small value-type commands plus a side
table of transforms. It is the renderer-facing contract that replaces v1's
`DisplayList`. The command set is deliberately small:

```
FillRect            rect, color
FillRoundedRect     rect, color, corner radius
DrawImage           rect, image id
DrawGlyphRun        origin, glyph-run id
PushClip / PopClip  rect
PushTransform / PopTransform   2x3 matrix (in the side table)
PushLayer / PopLayer           opacity, bounds
```

This is not a mirror of cascading style sheet (CSS) paint. It is the floor that every
renderer backend must support. CSS paint, charts, generated panels, and video all lower
into these. A backend that adds gradients or shadows extends the set. It does not get a
private command shape.

The commands are value types so a buffer of thousands costs one array, not thousands of
objects. This follows the C# performance policy in `AGENTS.md`. Variable-size data lives
in side tables and is reached by index, so each command stays small.

## D. RenderResourceTable

Commands reference images, glyph runs, and fonts by `ResourceId`, not by inlined data.
The renderer and compositor cache GPU resources by that id, so the same content is
uploaded once and reused across frames.

- Images dedup by content hash. The same image added twice returns the same id.
- Fonts dedup by a face key.
- Glyph runs are not deduped. Each draw is its own run.

`ResourceId` is scoped by kind. A `DrawImage` command's id indexes images. A
`DrawGlyphRun` command's id indexes glyph runs. The command kind tells the consumer
which table to read.

## E. Text

`Starling.Text` (Phase 2) owns font resolution, shaping, glyph runs, and the glyph
atlas policy. The scene model carries the result, not the work. A `GlyphRun` holds a
font id, a size, and a list of positioned glyphs. A `PositionedGlyph` is a face-local
glyph id and a pen offset. The renderer turns each glyph into a textured quad from the
atlas. The renderer never reshapes text.

## F. Hit regions

`HitRegionSet` is a list of rectangles, each mapped to a hit id. Input routing reads
this, not the paint commands, so a layer can be hit-tested without drawing it. The
topmost region wins on overlap.

## G. Provenance and permissions

`ProvenanceTag` records who produced a layer or region and what it may do. It carries a
source, an optional typed action id, and a `PermissionScope` of None, ReadOnly,
Interactive, or Privileged. A web document can leave this empty. A generated surface
sets it so the runtime can gate an action before it runs, instead of trusting an
arbitrary callback. This is the seam that keeps generated user interface out of
document-land.

---

## Acceptance Tests

1. What is the contract between the engine and the renderer, and which v1 type does it
   replace?
2. Why is the command set small, and what happens when a backend wants gradients?
3. How does the resource table avoid uploading the same image twice?
4. How can a layer be hit-tested without drawing it?
5. What does a generated panel set that a web document does not, and why?
