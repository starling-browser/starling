# v2 — Architecture

> **Scope (In):** The v2 module graph, the dependency rules, the native boundary, and
> the line between the kept v1 engine and the reset renderer.
> **Scope (Out):** The detail of each module. See the numbered docs and the migration doc.
> **Depends on:** [00_INDEX.md](00_INDEX.md).

---

## A. Module graph

Arrows point down. A module may only use modules below it.

```
                     ┌──────────────────┐
                     │  Starling.Shell   │  windowing, input, accessibility,
                     └────────┬─────────┘   permissions, MCP, telemetry
                              ▼
                     ┌──────────────────┐
                     │ Starling.Compositor│  layer tree, damage, tiles,
                     └────────┬─────────┘   resident textures, present
              ┌───────────────┼───────────────┐
              ▼               ▼               ▼
   ┌──────────────────┐ ┌───────────┐ ┌──────────────────┐
   │Starling.Renderer. │ │Starling.   │ │ Starling.Core     │
   │WebGpu (+Vello,    │ │Text        │ │ DOM, CSS, layout, │
   │ Blend2D later)    │ │            │ │ JavaScript, events│
   └────────┬─────────┘ └─────┬─────┘ └────────┬─────────┘
            ▼                  ▼                ▼
        ┌──────────────────────────────────────────┐
        │             Starling.Scene                 │
        │  SurfaceGraph, RenderScene, ResourceTable, │
        │  hit regions, provenance                   │
        └──────────────────────┬─────────────────────┘
                               ▼
                     ┌──────────────────┐
                     │   Starling.Gpu    │  WebGPU-shaped seam
                     └────────┬─────────┘
                              ▼
                  ┌────────────────────────┐
                  │ Starling.Gpu.WgpuNative │  the only native GPU code
                  │ (Starling.Gpu.Dawn later)│
                  └────────────────────────┘
```

`Starling.Core` is the v1 engine, reused. In code today it is the set of v1 projects
(`Starling.Dom`, `Starling.Css`, `Starling.Layout`, `Starling.Js`, and friends). v2
does not rewrite them. It lowers their output into `Starling.Scene`.

## B. Dependency rules

1. Arrows point down. No upward references. No cycles.
2. `Starling.Scene` depends on nothing but the base class library. It is the center,
   so it must stay free of GPU types, native types, and ImageSharp.
3. `Starling.Gpu` is interfaces and small value types only. It depends on nothing but
   the base class library. It does not depend on `Starling.Scene`.
4. Renderer code calls `Starling.Gpu`. Renderer code never calls wgpu-native, Silk.NET,
   or any raw graphics pointer.
5. Only `Starling.Gpu.WgpuNative` (and later `Starling.Gpu.Dawn`) knows about raw
   handles. It is the single place the native boundary is crossed for graphics.
6. The compositor owns present. A renderer backend draws into a texture or hands over
   an external surface. A backend never owns the whole frame.

## C. The native boundary

v1 locked "pure managed, no native." v2 keeps that for the engine and loosens it in
one place only.

- Pure managed, no change from v1: `Starling.Core` (DOM, CSS, layout, JavaScript,
  networking), `Starling.Scene`, `Starling.Gpu`, `Starling.Text` shaping logic.
- Native allowed: `Starling.Gpu.WgpuNative`, a later `Starling.Gpu.Dawn`, an optional
  `Starling.Renderer.Blend2D`, and the existing `Starling.Codecs` image-decode seam.

This matches the v1 interop policy in `AGENTS.md`: native interop lives at vetted
seams, not spread across the engine. v2 adds graphics as a second vetted seam next to
image decode. A continuous integration check should grep that no project except the
blessed list imports a native graphics binding.

wgpu-native crosses the foreign function interface, which is the call boundary between
managed code and the native library. wgpu releases a breaking version every three
months. Rule 5 turns that churn into work in one project instead of a change that
ripples through the renderer.

## D. Who owns what

| Concern | Owner | Replaceable part |
|---|---|---|
| Scene model | `Starling.Scene` | none. This is the core. |
| Text shaping and glyph atlas policy | `Starling.Text` | the shaper backend can change |
| GPU resource lifetime and present | `Starling.Compositor` over `Starling.Gpu` | the GPU provider (wgpu-native, then Dawn) |
| Drawing a RenderScene to a texture | `Starling.Renderer.WebGpu` | add `Starling.Renderer.Vello` or `Blend2D` later |
| Browser document semantics | `Starling.Core` | none |

## E. The critical boundary

The contract between the engine and the renderer must become:

```
SurfaceGraph + RenderScene + ResourceTable
```

It must not be any of these:

```
DisplayList to ImageSharp
Starling as a Vello scene builder
RenderScene that only a single backend understands
```

A browser document, a generated user-interface surface, a video layer, a devtools or
provenance overlay, a guest CEF or WebView surface, and a future Vello-rendered scene
are all surface producers. They all feed the same compositor through the same scene
model.

---

## Acceptance Tests

1. Can you name the one project that is allowed to call wgpu-native, and the rule that
   keeps every other project away from it?
2. Where does a video frame, a generated panel, and a web document meet so the
   compositor can draw them in one frame?
3. Why does `Starling.Scene` depend on nothing but the base class library?
4. Which v1 compositor pieces are kept, and which v1 renderer pieces are dropped?
