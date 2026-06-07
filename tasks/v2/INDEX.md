# Starling v2 — work index

This is the v2 (vnext) work queue. It sits beside the v1 queue in `tasks/`. v2 work
follows the same claim workflow as v1 (see [tasks/README.md](../README.md) and
[tasks/SCHEMA.md](../SCHEMA.md)). The plan it serves is in
[browser-plan/v2/](../../browser-plan/v2/00_INDEX.md).

v2 uses phase ids (`V2-P1` and up) instead of milestone numbers, because it is a hard
branch, not a continuation of the v1 milestone line. The v1 milestones (M0 to M13) keep
their own meaning.

## Phase 1 — core scene model and GPU seam

| Package | Status | Subsystem |
|---|---|---|
| [wp:V2P1-01-scene-model](P1/wp-V2P1-01-scene-model.md) | 🟡 in_review | Starling.Scene |
| [wp:V2P1-02-gpu-abstraction](P1/wp-V2P1-02-gpu-abstraction.md) | 🟡 in_review | Starling.Gpu |
| [wp:V2P1-03-trivial-scene](P1/wp-V2P1-03-trivial-scene.md) | 🟡 in_review | Starling.Scene.Tests, Starling.Gpu.Tests |

**in_review** here means the code is landed but has not been built or tested on a machine
with the .NET 11 preview software development kit (SDK). v2 targets .NET 11 with the C#
preview language version. The planning session had no SDK and no package feed. Run
`dotnet build src/v2/Starling.v2.slnx` and `dotnet test src/v2/Starling.v2.slnx` on a .NET 11 preview
SDK, then promote these to complete.

The shapes were finalized against the source design chat: a path-first scene IR with
brush handles, a LayerContent union with LayerId and content hash, a concrete GPU facade
over a single `IGpuBackend`, and an accessibility tree plus typed actions in the scene.

## Phase 2 and later — not yet broken into packages

These phases will be split into packages as v2 enters them, the same way v1 left M6 to
M9 for later. See [browser-plan/v2/04_MIGRATION.md](../../browser-plan/v2/04_MIGRATION.md).

- **Phase 2** — the C# WebGPU renderer. Draw API on `Starling.Gpu`,
  `Starling.Gpu.WgpuNative`, `Starling.Renderer.WebGpu`.
- **Phase 3** — lower v1 layout paint into RenderScene.
- **Phase 4** — semantic shell chrome compiled to RenderScene.
- **Phase 5** — `Starling.Renderer.Vello` for generated vector surfaces.
- **Phase 6** — `Starling.Gpu.Dawn` or `Starling.Renderer.Blend2D` if needed.
