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
       ├─ id            stable LayerId, assigned by the graph
       ├─ content       RenderScene | ExternalTexture | Video | NativeGuest
       ├─ contentHash   backend-neutral cache key for the compositor
       ├─ transform, opacity, clip   compositing parameters, read per frame
       ├─ provenance    who made it, what typed action it may run
       ├─ accessibility a node tree, for inspection and screen readers
       ├─ hit regions   decoupled from paint
       └─ (RenderScene only)
            ├─ RenderCommandBuffer   the path-first draw commands, in paint order
            └─ RenderResourceTable   paths, brushes, images, glyph runs, fonts, by id
```

The key split is content versus compositing parameters. A layer's content is its
pixels-to-be. Its transform, opacity, and clip are read every frame and applied at
composite time. A move or a fade does not rebuild the content. This is the same idea
the v1 layer-tree work proved, lifted into the core model.

## B. SurfaceGraph and SurfaceLayer

`SurfaceGraph` holds the surface size in device-independent pixels, the scale (the
device pixel ratio), and an ordered list of layers from bottom to top. It assigns each
layer a stable `LayerId`. The compositor consumes one graph per frame.

`SurfaceLayer.Content` is a closed union, not always a render scene:

- `RenderSceneContent` — a document, generated UI, a chart, drawn from commands.
- `ExternalTextureContent` — a texture another renderer backend produced.
- `VideoContent` — a video frame source.
- `NativeGuestContent` — a CEF or WebView guest surface. CEF is the Chromium Embedded
  Framework.

A video or guest layer has no commands, so forcing a render scene on every layer would
be wrong. The union models content honestly. It is written as a C# 15 union type; that
syntax is preview and has not been built against the .NET 11 preview software development
kit yet, so `LayerContent.cs` carries the sealed-record closed-hierarchy fallback in a
comment for a one-step revert.

Each layer also carries a `ContentHash`. The compositor pairs `LayerId` with
`ContentHash` to reuse a resident texture and to compute damage, so an unchanged layer
is not re-rendered. This is what keeps the v1 resident-texture caching working in v2.

## C. RenderScene, RenderCommandBuffer, RenderCommand

`RenderScene` is a `RenderCommandBuffer` plus a `RenderResourceTable`.

`RenderCommandBuffer` is an append-only list of small value-type commands plus a side
table of transforms. It is the renderer-facing contract that replaces v1's
`DisplayList`. The command set is **path-first**: geometry is always a path, paint is
always a brush. There is no `FillRect` or `FillRoundedRect` opcode.

```
FillPath        path id, brush id
StrokePath      path id, brush id, stroke width
DrawGlyphRun    origin, glyph-run id, brush id
DrawImage       rect, image id
PushClip        path id
PopClip
PushTransform   2x3 matrix (in the side table)
PopTransform
PushLayer       opacity, bounds
PopLayer
SetBlendMode    blend mode
```

A rectangle and a rounded rectangle are paths built by `Path.Rect` and
`Path.RoundedRect`. So the renderer has one geometry primitive to support, the command
set stays small, and the intermediate representation maps cleanly onto Vello later. CSS
paint, charts, generated panels, and video all lower into these commands.

The commands are value types so a buffer of thousands costs one array, not thousands of
objects. This follows the C# performance policy in `AGENTS.md`. Variable-size data
(paths, brushes, glyph runs) lives in the resource table and is reached by id, so each
command stays small.

## D. RenderResourceTable

Commands reference geometry and paint by typed id, not by inlined data, so the renderer
and compositor cache GPU resources by that id.

- `PathId` — a vector path (rect, rounded rect, or arbitrary path).
- `BrushId` — a paint source: solid, linear gradient, or image.
- `ImageId` — a decoded image. Dedups by content hash, so the same image uploads once.
- `FontId` — a resolved font face. Dedups by face key.
- `GlyphRunId` — a shaped glyph run. Not deduped: each draw is its own run.

Typed ids mean a command can never reference an image where it meant a path.

## E. Brushes and paths

A `Brush` is the paint for a fill or stroke. Making paint a brush handle, not a separate
`FillGradient` or `FillImage` command, is what keeps the command set small. A brush is
`Solid` (a color), `LinearGradient` (endpoints plus stops), or `Image` (an image id).

A `Path` is a list of verbs (move, line, quadratic, cubic, close) and the points they
consume. `PathBuilder` builds arbitrary geometry. `Path.RoundedRect` uses cubic-bezier
corners.

## F. Text

`Starling.Text` (Phase 2) owns font resolution, shaping, glyph runs, and the glyph
atlas policy. The scene model carries the result, not the work. A `GlyphRun` holds a
font id, a size, and a list of positioned glyphs. A `PositionedGlyph` is a face-local
glyph id and a pen offset. The renderer turns each glyph into a textured quad from the
atlas, painted with the brush on the `DrawGlyphRun` command. The renderer never
reshapes text.

## G. Hit regions and accessibility

`HitRegionSet` is a list of rectangles, each mapped to a hit id. Input routing reads
this, not the paint commands, so a layer can be hit-tested without drawing it. The
topmost region wins on overlap.

`AccessibilityNode` is a tree on the layer: a role, a name, a value, bounds, children,
and an optional link to a hit region by id. The scene model owns it so a surface is
inspectable and reachable by a screen reader without rendering it. The same geometry can
drive both pointer and assistive-technology interaction.

## H. Provenance and typed actions

`ProvenanceTag` records who produced a layer or region and what it may do. It carries a
source, a `PermissionScope` (None, ReadOnly, Interactive, Privileged), and an optional
`ActionRef`. An `ActionRef` is a typed action: the tool it invokes (for example
`calendar.createEvent`), whether it needs confirmation, and a label. It names a tool,
not an arbitrary callback, so the runtime can validate and gate it. This mirrors the
action shape in a SurfaceSpec and is the seam that keeps generated UI out of
document-land. A web document can leave provenance empty.

---

## Acceptance Tests

1. What is the contract between the engine and the renderer, and which v1 type does it
   replace?
2. Why is the command set path-first, and where do rectangles come from?
3. How does a video layer differ from a document layer in the model?
4. What two values let the compositor skip re-rendering an unchanged layer?
5. How can a layer be hit-tested and read by a screen reader without drawing it?
6. What does a generated panel set that a web document does not, and why is it typed?
