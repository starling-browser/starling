# Starling v2 — Master Plan Index

> **Codename:** Starling v2 (vnext). A hard branch from the v1 engine. v1 proved a
> pure-managed browser. v2 keeps that engine and resets the renderer so Starling
> owns its own surface model, scene model, text model, GPU lifetime, and compositor.
>
> **Through-line:** Starling owns the SurfaceGraph, RenderScene, ResourceTable, text
> model, and compositor. Vello, wgpu-native, Dawn, Blend2D, and CEF or WebView are
> replaceable choices underneath or beside that core. CEF is the Chromium Embedded
> Framework. None of them get to define Starling's architecture.
>
> **Chosen path:** Path A (Foundation A). A Starling-owned C# renderer over a small
> GPU seam. wgpu-native is the first GPU provider. Dawn is a later provider. Vello is
> a later renderer backend. Blend2D is an optional processor fallback.

---

## How to use this plan

1. Read [01_ARCHITECTURE.md](01_ARCHITECTURE.md) first. It draws the module graph and the
   rules every module follows.
2. [02_SCENE_MODEL.md](02_SCENE_MODEL.md) and [03_GPU_ABSTRACTION.md](03_GPU_ABSTRACTION.md)
   are the specs for the two Phase 1 projects that already exist in code.
3. [04_MIGRATION.md](04_MIGRATION.md) is the phase order and the map from v1 types to v2
   types. Read it before you move any v1 code.
4. Work items live in [tasks/v2/INDEX.md](../../tasks/v2/INDEX.md).

This plan sits beside the v1 plan in `browser-plan/`. The v1 docs still describe the
shipping engine. v2 does not edit them. v2 starts in parallel, and v1 keeps building
the whole time.

---

## What v2 keeps from v1

v1 is not just a shell. It already has a real browser engine split: networking, the
URL parser, the Starling HTML parser, the Starling DOM, CSS, layout, the Starling
JavaScript engine, paint, the engine host, the GraphicalUserInterface (GUI) shell, the
native shell, the Model Context Protocol (MCP) server, and telemetry. v2 keeps all of
that work.

The strongest v1 lesson is the compositor. v1 already owns a WebGPU surface, keeps
layer textures resident on the GPU, draws the final frame into the surface texture,
and never reads pixels back to the processor. v2 carries that direction forward.

| Kept from v1 | Where it lives today |
|---|---|
| Managed browser and runtime engine | `src/Starling.{Net,Url,Html,Dom,Css,Layout,Js,Bindings,Engine}` |
| Layer and compositor model | `src/Starling.Paint/Compositor/` |
| Resident texture cache, tile cache | `GpuBlendEngine`, `TileGrid` |
| Zero-readback present | `GpuSurfacePresenter` |
| Frame packet, texture retirement | `compositor-thread-scope.md` |
| MCP, telemetry, control plane | `src/Starling.Mcp`, `src/Starling.Telemetry` |

---

## What v2 resets

| Topic | v1 decision | v2 decision |
|---|---|---|
| Renderer contract | `DisplayList` to bitmap or texture | SurfaceGraph + RenderScene + ResourceTable |
| Rasterization | ImageSharp.Drawing 3 | A Starling-owned WebGPU renderer. ImageSharp leaves the critical path. |
| Native code | None. Pure managed everywhere. | Native is allowed only in `Starling.Gpu.*` backends. The engine core stays pure managed. |
| GPU access | Silk.NET WebGPU pointers used inside the compositor | All GPU calls go through the `Starling.Gpu` seam. Only `Starling.Gpu.WgpuNative` touches raw handles. |
| Per-layer raster | `DisplayList` lowered through ImageSharp's WebGPU target by reflection | RenderScene drawn by `Starling.Renderer.WebGpu` with Starling-owned shaders. |
| Shell chrome | HyperText Markup Language (HTML) strings built at runtime | Semantic components compiled straight to RenderScene (Phase 4). |
| Generated UI | Not present in v1 | A constrained SurfaceSpec, not generated HTML (later phase). |

The rule behind every row: Starling owns the scene model and the compositor. The
GPU library, the renderer backend, and any guest runtime are replaceable parts under
that core.

---

## Documents

| # | File | What is in it |
|---|---|---|
| 00 | [00_INDEX.md](00_INDEX.md) | This page. |
| 01 | [01_ARCHITECTURE.md](01_ARCHITECTURE.md) | Module graph, dependency rules, the native boundary. |
| 02 | [02_SCENE_MODEL.md](02_SCENE_MODEL.md) | SurfaceGraph, RenderScene, ResourceTable, hit regions, text, provenance. |
| 03 | [03_GPU_ABSTRACTION.md](03_GPU_ABSTRACTION.md) | The `Starling.Gpu` seam and the backend rule. |
| 04 | [04_MIGRATION.md](04_MIGRATION.md) | Phase order, v1-to-v2 type map, the golden gate. |

---

## Locked decisions for v2 (do not relitigate)

| Topic | Decision | Source |
|---|---|---|
| Foundation | Path A. A C# renderer core. The renderer core is not Rust or Vello. | user |
| Platform | .NET 11, C# preview language version. New features (for example C# 15 union types) allowed for correctness modeling. | user |
| First GPU provider | wgpu-native, behind the `Starling.Gpu` seam. | user |
| Native boundary | Native lives only in `Starling.Gpu.*` backends and the existing `Starling.Codecs` seam. | user |
| Repo layout | A parallel tree. v2 code under `src/v2/`. v1 keeps building. | user |
| Scene IR | Path-first commands (FillPath/StrokePath) with brush handles. Not a FillRect menu, not DisplayList. | user |
| Layer content | A closed union: render scene, external texture, video, or native guest. Plus LayerId and content hash. | user |
| GPU seam | Concrete facade classes over a single `IGpuBackend`. WebGPU init chain. | user |
| Scene owner | Starling. Not ImageSharp, not Vello, not wgpu, not Chromium. | this plan |
| Present owner | The Starling compositor. Backends render into textures or external surfaces. | this plan |

---

## Status

| Date | Author | Note |
|---|---|---|
| 2026-06-07 | Claude (v2 planning pass) | Hard-branch plan for Path A. Phase 1 projects landed: `Starling.Scene`, `Starling.Gpu`, with tests. |
| 2026-06-07 | Claude (design finalization) | Reconciled against the source design chat. Reworked Phase 1 to the decided shapes: path-first scene IR with brush handles, LayerContent union plus LayerId and content hash, concrete GPU facade over one `IGpuBackend`, accessibility tree and typed actions in the scene. Retargeted v2 to .NET 11 and C# preview. Pending a build on a machine with the .NET 11 preview SDK. |
