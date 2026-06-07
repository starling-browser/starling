# v2 — Migration

> **Scope (In):** The phase order, the map from v1 types to v2 types, the parallel-tree
> rule, and the golden gate that keeps v2 honest.
> **Scope (Out):** The per-type detail. See the scene and GPU docs.
> **Depends on:** [01_ARCHITECTURE.md](01_ARCHITECTURE.md), [02_SCENE_MODEL.md](02_SCENE_MODEL.md),
> [03_GPU_ABSTRACTION.md](03_GPU_ABSTRACTION.md).

---

## A. The parallel-tree rule

v2 code lives under `src/v2/` with its own solution, `Starling.v2.slnx`. v1 keeps
building the whole time. This is on purpose. v1 is the oracle. Each v2 piece is correct
when it matches v1 output on the same input, so v1 has to stay runnable until v2 can
stand alone.

A v2 project is pure managed unless it is a `Starling.Gpu.*` backend. The v2 solution
builds with no native prerequisite and no Six Labors license, because `Starling.Scene`
and `Starling.Gpu` touch neither ImageSharp nor a native graphics library.

v2 targets **.NET 11** with the **C# preview** language version, so it can use new
features (for example C# 15 union types) for correctness and exhaustiveness modeling,
per the performance policy in `AGENTS.md`. The repo-root `global.json` rolls forward to
the latest major and allows prerelease, so a .NET 11 preview software development kit
builds v2 while the v1 solution still builds on the .NET 10 kit. The v1 continuous
integration job builds `Starling.slnx`, not `Starling.v2.slnx`, so it is unaffected.

## B. Phase order

### Phase 1 — Drop ImageSharp as the boundary. Build the core. (done)

Create the core types before the renderer exists: `SurfaceGraph`, `RenderScene`,
`ResourceTable`, and the `Starling.Gpu` seam. Build a trivial scene in tests: solid
rects, rounded rects, images, glyph quads, basic clipping, and a surface present path.

Shipped: `src/v2/Starling.Scene`, `src/v2/Starling.Gpu`, and their tests. The scene IR
is path-first (FillPath/StrokePath with brush handles, not a FillRect menu). A layer's
content is a closed union (render scene, external texture, video, or native guest), with
a LayerId and a content hash for compositor caching. The scene carries an accessibility
tree and typed actions. The GPU seam is concrete facade classes over a single
`IGpuBackend`. The trivial scene is a unit test that builds a card surface with every
primitive. The present path is proven with a recording fake backend.

### Phase 2 — Build the C# WebGPU renderer

Start narrow: rect, rounded rect, image, glyph atlas, linear gradient, opacity,
transform, clip and scissor, and layer-texture composition. Do not start with arbitrary
path rendering. This phase adds the draw API to `Starling.Gpu`
(pipelines, bind groups, draw calls), lands `Starling.Gpu.WgpuNative`, and lands
`Starling.Renderer.WebGpu` that walks a `RenderCommandBuffer` and draws.

### Phase 3 — Lower v1 layout into RenderScene

Keep the useful v1 engine pieces. Lower their paint output into the new scene model:
the v1 box tree paints into a RenderScene instead of a `DisplayList`. Over time the
`DisplayList` shrinks to a CSS-paint convenience layer or disappears.

### Phase 4 — Rebuild shell chrome as semantic components

v1 chrome is built from HTML strings at runtime. That was clever dogfooding. v2 chrome
becomes semantic components compiled straight to a RenderScene, with real hit regions
and provenance.

### Phase 5 — Add Vello as an experimental backend

Add `Starling.Renderer.Vello` as a second backend behind the same RenderScene contract.
Start with generated vector surfaces, not arbitrary web pages: diagrams, charts,
whiteboards, vector panels, animated cards. Vello renders a surface. Vello does not
define the surface model.

### Phase 6 — Add Dawn or Blend2D if needed

Add `Starling.Gpu.Dawn` if wgpu-native churn becomes painful. Add
`Starling.Renderer.Blend2D` if a processor fallback or a golden-test path becomes
important. Both fit under the same seams with no renderer changes.

## C. v1-to-v2 type map

| v1 today | v2 replacement | Note |
|---|---|---|
| `DisplayList` + `DisplayItem` (`Starling.Paint/DisplayList`) | `RenderCommandBuffer` + `RenderCommand` | path-first (FillPath/StrokePath), brushes as handles, value-type commands |
| `IPaintBackend` (DisplayList to `RenderedBitmap`) | `Starling.Renderer.*` over `Starling.Gpu` | the renderer-facing contract becomes RenderScene |
| `IGpuTexturePaintBackend` via ImageSharp `WebGPURenderTarget` (reflection) | `Starling.Renderer.WebGpu` with Starling shaders | no reflection bridge, no ImageSharp |
| `CompositorLayer` (`Starling.Paint/Compositor`) | `SurfaceLayer` (LayerContent union) + `LayerId` + `ContentHash` | content split from compositing parameters; cache key kept |
| `CompositedFrameRequest` (`Starling.Gui.Core/Rendering`) box-tree roots | `SurfaceGraph` of `SurfaceLayer` | every root becomes a surface producer |
| `GpuBlendEngine`, `GpuSurfacePresenter` (raw Silk.NET) | `Starling.Compositor` over `Starling.Gpu` facade + `Starling.Gpu.WgpuNative` (`IGpuBackend`) | raw handles move behind the seam |
| `FramePacket`, texture retirement (`compositor-thread-scope.md`) | same idea, on `Starling.Gpu` | carried forward, not reset |
| HTML-string chrome (`NativeBrowserWindow.cs`) | semantic chrome components to RenderScene | Phase 4 |

## D. The golden gate

v1 already proves a render is correct with byte-identical golden images. v2 reuses that.
A v2 renderer pass is correct when its output matches v1 at the same clock on the same
input, within the golden tolerance. The parallel tree exists so this comparison is
always possible. No v1 code is deleted until the matching v2 piece passes the gate.

## E. What v2 will not do

- No second life for ImageSharp at the center.
- No Vello as the Starling public interface.
- No raw wgpu-native calls outside the backend project.
- No HTML as the generated-user-interface contract.
- No CEF or WebView as the root compositor. They are guest surfaces only.
- No Direct3D 12, Metal, or Vulkan backend from day one.

---

## Acceptance Tests

1. Why does v1 have to keep building during the v2 migration?
2. What is the order of the six phases, and which one is already done?
3. What replaces `CompositedFrameRequest`, and what replaces `DisplayList`?
4. When is a piece of v1 code allowed to be deleted?
5. Name three things v2 will not do.
