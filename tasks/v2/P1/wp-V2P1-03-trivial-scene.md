---
id: "wp:V2P1-03-trivial-scene"
parent: ""
milestone: "V2-P1"
status: "complete"
completed_at: "2026-06-07T16:28:00Z"
claimed_by: "agent-claude-v2planning"
claimed_at: "2026-06-07T00:00:00Z"
branch: "claude/starling-v2-architecture-xnwsP"
depends_on:
  - "wp:V2P1-01-scene-model"
  - "wp:V2P1-02-gpu-abstraction"
blocks: []
subsystem: "Starling.Scene.Tests"
plan_refs:
  - "browser-plan/v2/02_SCENE_MODEL.md"
  - "browser-plan/v2/03_GPU_ABSTRACTION.md"
  - "browser-plan/v2/04_MIGRATION.md"
---

# wp:V2P1-03 — trivial scene and present path

## Goal
Prove the Phase 1 core holds together. Build a trivial scene in tests that uses every
primitive the v2 intermediate representation ships with, and drive the GPU seam through
a full present path with a recording fake device. This is the Phase 1 exit in the
migration doc.

## Inputs
- wp:V2P1-01-scene-model and wp:V2P1-02-gpu-abstraction.

## Outputs
- `tests/v2/Starling.Scene.Tests/` — `RenderCommandBufferTests.cs`,
  `RenderResourceTableTests.cs`, `HitRegionSetTests.cs`, `SurfaceGraphTests.cs`,
  `TrivialSceneTests.cs`
- `tests/v2/Starling.Gpu.Tests/` — `Fakes/FakeGpuBackend.cs` (recording `IGpuBackend`)
  and `GpuAbstractionTests.cs`
- `src/v2/Starling.v2.slnx`, wiring the two source projects and two test projects (all
  net11.0, C# preview). It sits in `src/v2/`, not the repo root, so the v1 continuous
  integration job's single-solution auto-discovery still finds only `Starling.slnx`.

## Acceptance
- The trivial scene test builds a card surface with a filled rounded-rect path, an
  image, a glyph run with a brush, and a clip, plus a hit region and an accessibility
  node that route to a typed action.
- The command-buffer test proves path-first commands (FillPath/StrokePath/SetBlendMode)
  carry path and brush handles correctly.
- The resource table test proves an image added twice returns one id and uploads once,
  and that paths and brushes round-trip.
- The surface-graph test proves layers get distinct ids and that a video or guest layer
  has no render scene.
- The hit-region test proves the topmost region wins and a miss returns null.
- The GPU test records the present path in order over the facade: create instance,
  surface, adapter, device, queue, buffer, texture, configure, acquire, view, encode,
  render pass clear, render pass end, finish, submit, present, plus release of owned
  resources.
- `cd src/v2 && dotnet test Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- The fake backend proves the GPU seam is implementable with no native code in the loop.
- Tests follow the repo convention: MSTest on the Microsoft Testing Platform with
  AwesomeAssertions, snake_case test names.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. The code is written to
  the repo's strict settings (warnings as errors, the analyzer set, file-scoped
  namespaces, static lambdas, no unused usings). It was not built or run: the planning
  environment had no .NET SDK and the package host was blocked.
- 2026-06-07T01:00Z — design finalization. Rewrote the tests for the path-first IR, the
  LayerContent union, the accessibility tree, typed actions, and the concrete GPU facade
  over a single `IGpuBackend`. Retargeted to .NET 11 and C# preview. First action for
  the next session: `cd src/v2 && dotnet build Starling.v2.slnx` then `cd src/v2 && dotnet test Starling.v2.slnx`
  on a .NET 11 preview SDK, fix any analyzer fallout, then promote V2P1-01..03 to
  complete.
- 2026-06-07T16:28Z — the net11 CI job built and ran the v2 tests green on the .NET 11
  preview SDK (commit ec4ac91): the trivial scene, resource, hit-region, surface-graph,
  and GPU present-path tests all pass. Marking complete.
