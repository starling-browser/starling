---
id: "wp:V2P1-03-trivial-scene"
parent: ""
milestone: "V2-P1"
status: "in_review"
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
- `tests/v2/Starling.Gpu.Tests/` — `Fakes/` recording backend and `GpuAbstractionTests.cs`
- `Starling.v2.slnx` at the repo root, wiring the two source projects and two test
  projects.

## Acceptance
- The trivial scene test builds a card surface with a rounded rect, an image, a glyph
  run, a fill, and a clip, plus a hit region that routes a click to a typed action.
- The resource table test proves an image added twice returns one id and uploads once.
- The hit-region test proves the topmost region wins and a miss returns null.
- The GPU test records the present path in order: write buffer, write texture, configure,
  acquire, create view, encode, render pass clear, render pass end, finish, submit,
  present, plus dispose of owned resources.
- `dotnet test Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- The fake device proves the seam is implementable with no native code in the loop.
- Tests follow the repo convention: MSTest on the Microsoft Testing Platform with
  AwesomeAssertions, snake_case test names, one type per file.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. The code is written to
  the repo's strict settings (warnings as errors, the analyzer set, file-scoped
  namespaces, static lambdas, no unused usings). It was not built or run: the planning
  environment had no .NET 10 SDK and the package host was blocked. First action for the
  next session: `dotnet build Starling.v2.slnx` then `dotnet test Starling.v2.slnx`, fix
  any analyzer fallout, then promote V2P1-01..03 to complete.
