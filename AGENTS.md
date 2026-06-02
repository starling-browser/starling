# Agents — rules of engagement

This file is the **first thing an implementation agent should read** when
opening this repo. It explains how multiple agents share work without
stepping on each other, and how a single agent can stop mid-task and have a
later session resume cleanly.

## TL;DR

```bash
# 1. Orient
cat AGENTS.md                            # this file
less browser-plan/13_MILESTONES.md       # where we are
less tasks/INDEX.md                      # what's available

# 2. Claim something unblocked
./tasks/lib/claim.sh claim wp:M1-03-dom-core "agent-claude-<your-handle>"

# 3. Read the package file and start working on main
less tasks/M1/wp-M1-03-dom-core.md

# 4. Commit often, with the wp id in the subject:
#    "wp:M1-03 — Node hierarchy + tests"

# 5. Build + test before completing
dotnet build && dotnet test

# 6. Mark complete:
./tasks/lib/claim.sh complete wp:M1-03-dom-core
```

**All work happens on `main`.** This repo doesn't use a per-package branch
workflow — agents commit directly to main. (If a remote with PR review is
ever wired up, the optional `in_review` state in `tasks/SCHEMA.md` is
available for it; today it's unused.)

If you have to stop early, **leave a handoff log entry** in the task file
and either keep the claim (you'll resume) or release it
(`./tasks/lib/claim.sh release wp:…`). Either way: commit so the next
agent sees the state.

## The contract

| You can rely on | You must do |
|---|---|
| One file per work package under `tasks/M*/wp-*.md` | Touch only your claimed package's file (plus your code changes) |
| `tasks/INDEX.md` reflects the current status | Update INDEX.md when you change a status |
| Work happens on `main`; commits carry the wp id in the subject | Prefix commit messages with `wp:<id> —` so history is greppable |
| Inbound contributions require a CLA | Make sure the contributor has a CLA on file before the work lands |
| Dependencies are explicit in `depends_on` | Don't start a package whose deps aren't `complete` |
| Stale claims age out at 72 h | Add a handoff log entry every session, even if you're "still going" |

## Contribution provenance

Inbound contributions require a signed Contributor License Agreement. The CLA
text is in `CLA.md`, and the hosted CLA Assistant setup notes are in
`CLA_ASSISTANT.md`.

Direct commits to `main` should only be made by people who already have a CLA on
file. Bots should be listed in the CLA Assistant allowlist.

AI-assisted work is allowed. The contributor is responsible for the change,
including license, security, and correctness review. If you add a generated
port, translated code, parser table, large generated file, or copied algorithm,
call it out in the PR or handoff log. Small autocomplete-style suggestions do
not need a disclosure.

Add `// SPDX-License-Identifier: Apache-2.0` to new owned C# files. Do not add
Starling SPDX headers to vendored data, generated files, submodules, or
third-party assets.

## Repo map

```
starling/
├── AGENTS.md                  ← you are here
├── .mcp.json                  ← Aspire Model Context Protocol wiring (`aspire agent mcp`)
├── README.md                  ← human-facing intro
├── browser-plan/              ← the design (immutable except by deliberate edit)
│   ├── 00_INDEX.md            ← start here for design context
│   ├── 13_MILESTONES.md       ← what milestone we're in
│   └── 14_AGENT_TASKS.md      ← authoritative package catalog
├── tasks/                     ← work coordination (this repo's queue)
│   ├── README.md              ← detailed workflow
│   ├── SCHEMA.md              ← frontmatter contract
│   ├── INDEX.md               ← current status of all packages
│   ├── lib/claim.sh           ← atomic claim/release helper
│   └── M<n>/wp-*.md           ← one file per work package
├── lib/starling-regexp/       ← JS RegExp engine submodule used by Starling.Js
├── src/                       ← engine + native decode seam + hosts + tools
│   ├── Starling.AppHost/          ← Aspire AppHost (orchestrates Gui + Headless)
│   ├── Starling.Codecs/           ← OS-native image decode interop seam
│   ├── Starling.Mcp/              ← loopback MCP server + shared tool plumbing
│   ├── Starling.ServiceDefaults/  ← Aspire OTel + health-check shared bootstrap
│   └── Starling.Telemetry/        ← OpenTelemetry bootstrap + in-memory sinks
├── tests/                     ← MSTest projects per module, plus Avalonia headless xUnit
├── bench/Starling.Bench/      ← BenchmarkDotNet + frame replay (see bench/README.md)
└── testdata/                  ← fixtures + golden PNGs + WPT subsets
```

## Build + test (must be green before merge)

Before the first restore, make sure the `Starling.RegExp` submodule is present.
`src/Starling.Js` references `lib/starling-regexp/src/Starling.RegExp/`, so an
empty submodule breaks restore and build.

```bash
git submodule update --init --recursive
```

Paint and raster paths need a Six Labors license. Local builds read
`sixlabors.lic` from the repo root through `Directory.Build.props`. CI uses the
`SIXLABORS_LICENSE_KEY` secret.

```bash
dotnet --version            # expect 10.0.x
dotnet restore
dotnet build -c Debug
dotnet test  -c Debug
```

Most test projects use MSTest 4 on Microsoft Testing Platform. The exception is
`tests/Starling.Gui.Headless.Tests`, which uses `xunit.v3` because
`Avalonia.Headless.XUnit` requires it. Run GUI checks with:

```bash
dotnet test tests/Starling.Gui.Tests           # needs a display
dotnet test tests/Starling.Gui.Headless.Tests  # no display needed
```

If `dotnet build` errors with permission-denied apphost deletions in a
sandbox or container, pass `-p:UseAppHost=false`. This is a sandbox quirk
only — CI runs without the flag.

## Getting traces & telemetry from Aspire

When you need to debug runtime behavior — a slow request, a failing fetch, an
unexpected span — pull traces from the Aspire dashboard's telemetry API instead
of adding `Console.WriteLine`. **This only works while the AppHost is running**
(`aspire run` against `src/Starling.AppHost`, or via the `aspire` skill); the
telemetry API is served by that running dashboard.

The repo-root `aspire.config.json` points at `src/Starling.AppHost`, so
`aspire run` from the repo root starts the `gui` resource and the `sites` static
resource. `headless` is registered with explicit start and is launched from the
dashboard when needed. `sites` serves `testdata/sites/` at
`http://localhost:8088/`. Restart the resource after editing those fixtures.

Runtime selection flags go after `aspire run --` and are forwarded to both GUI
and headless resources:

```bash
aspire run -- --jint --imagesharp     # Jint JS backend + CPU paint
aspire run -- --starling --gpu        # Starling JS backend + WebGPU paint
```

Flags win over `STARLING_JS_ENGINE` and `STARLING_PAINT_BACKEND`.

There are three ways in, listed in the order you should reach for them.

**1. MCP tools (preferred — already wired in `.mcp.json` via `aspire agent mcp`).**
No setup; just call them. The drill-down path:

- `mcp__aspire__list_traces` — list distributed traces (trace IDs, the resources
  each spans, duration, error flag). Optional `resourceName` narrows to one
  resource; omit it for all. Use this first to find the trace ID you care about.
- `mcp__aspire__list_trace_structured_logs` — given a `traceId`, the structured
  logs for that trace, grouped by span. **Prefer this over per-resource logs
  when investigating a specific trace.**
- Supporting tools: `mcp__aspire__list_structured_logs`,
  `mcp__aspire__list_console_logs`, `mcp__aspire__list_resources`.

Typical loop: `list_traces` (optionally filtered to a resource) → pick the
`traceId` → `list_trace_structured_logs` for span-level detail.

**2. Aspire CLI (`aspire otel`).** Same telemetry API, handy for ad-hoc shell
use and for full OTLP JSON you can pipe to `jq`:

```bash
aspire otel traces [<resource>] --format Json   # list traces (Table is default)
aspire otel traces -t <trace-id> --format Json   # one trace's spans, full detail
aspire otel traces --has-error -n 20             # only failing traces, cap 20
aspire otel spans  <resource>  --format Json     # raw spans for a resource
aspire otel logs   <resource>  --format Json     # structured logs
```

Note the local CLI (Aspire 13.3.x) uses `-t/--trace-id` to select a trace (not a
positional id) and `-n/--limit` to cap results; there is no `--search` flag. For
a standalone dashboard, pass `--dashboard-url http://localhost:18888`
(and `--api-key` if it requires one).

**3. Raw REST API.** If you want the complete OTLP `ResourceSpans` payload
programmatically, hit the dashboard directly (auth via the dashboard API token):
`GET /api/telemetry/traces?resource=&hasError=&limit=` for the list and
`GET /api/telemetry/traces/{traceId}` for one full trace. The CLI and MCP tools
are wrappers over these.

Starling also exposes its own Model Context Protocol (MCP) server. The GUI
serves browser-control tools from `src/Starling.Gui/Mcp/` plus telemetry tools
from `src/Starling.Mcp/Telemetry/`. Direct GUI runs default to
`http://127.0.0.1:3077/mcp`. AppHost sets `STARLING_MCP_URL` to
`http://127.0.0.1:3078/mcp`. Headless only starts its telemetry MCP server when
`STARLING_HEADLESS_MCP_URL` is set.

## Spec coverage & the bug-fix workflow

Most bugs in this engine are a spec compliance gap wearing a disguise. Before
you "just fix it", run this loop. It is **not optional** — a fix without a
test is not done.

1. **Check whether the behavior is covered by a spec.** Identify the spec and
   section the buggy behavior belongs to (CSS 2.x / Sizing / Flexbox / DOM /
   HTML / URL …). `tasks/SPEC_COVERAGE.md` is the map of where we stand;
   `tasks/SPEC_CATALOG.md` is the upstream list of what each spec defines.
2. **Check whether we already have a test for it.** Grep the `[Spec]` traits:
   `dotnet test --filter "TestCategory~Spec:<spec-id>"`, or grep the source
   for `[Spec("<spec-id>"`. If a `[PendingFact]` already documents this gap,
   you're about to promote it — don't write a duplicate.
3. **Reproduce with a failing test first.** When you report or start a bug,
   write the test that demonstrates the failure *before* touching the fix.
   Tag it `[Spec(id, url, section)]`. If you can't make it pass yet, commit it
   as `[PendingFact]` with a `trackingWp`; if you're fixing it now, watch it
   go red, then green.
4. **Every fix ships with a test.** The test that reproduced the bug becomes
   the regression test — promote it to `[SpecFact]` in the same change as the
   fix. Put it where the exercised code is tested (e.g. layout behavior →
   `Starling.Layout.Tests`), not in a catch-all. No "fixed it, trust me" —
   the diff must contain a test that fails without your change.

There is **one** way spec tests are tracked: real test methods tagged with
`[Spec]` + `[SpecFact]`/`[PendingFact]` from `Starling.Spec.Common`. There is
no stub generation. See `tests/Starling.Spec.Common/README.md`.

The large upstream suites are opt-in because their corpora are not committed.
Use `tools/fetch-test262.sh` before `dotnet test tests/Starling.Js.Test262.Tests`
and `tools/fetch-wpt.sh` before `dotnet test tests/Starling.Wpt.Tests`. If a
corpus is absent, those tests skip or report inconclusive instead of failing the
normal build.

## Interop policy — managed-first, native at vetted seams

Native interop (`[LibraryImport]`/`[DllImport]`) is confined to one
**designated interop project**: `src/Starling.Codecs` (image decode). Every
platform backend lives there: ImageIO on macOS, WIC on Windows, and
libpng/libjpeg/libwebp on Linux. It is also the only engine project with
`AllowUnsafeBlocks=true`. Every other engine module under
`src/Starling.{Common,Url,Net,Html,Dom,Css,Layout,Paint,Js,Bindings,Mcp,Telemetry,Engine}/`
stays **pure managed** — no P/Invoke, no native dependencies beyond what the
.NET BCL ships. **TLS path: BouncyCastle.** `Starling.Net` uses
`BouncyCastle.Cryptography` (pure-managed, no P/Invoke) for TLS 1.3 via
`BcTlsTransport`. The `wp:M3-06e` SslStream migration was rolled back in
`939f3a5 fix ssl crash` (2026-05-14) after a macOS TLS 1.3 issue surfaced in
integration; re-attempting SslStream — or formally re-blessing BouncyCastle as
the long-term path — is a tracked open item in `wp:M3-06-native-interop-pivot`'s
handoff log. The interop-seam policy is still satisfied either way, because
BouncyCastle adds no native dependency. CI greps the engine-project allowlist
(every engine project *except* the Codecs interop project); the lint job fails
if you regress it. The GUI shell (`src/Starling.Gui`, Avalonia 12) and the Aspire
AppHost/ServiceDefaults projects are exempt — they link against Avalonia desktop
backends and ASP.NET host plumbing respectively, which is fine because the
engine never imports from any of them. The engine projects must continue to
build and test cleanly without those heavier platforms loaded.

**Paint backend: ImageSharp.Drawing 3 (managed).** The previous native
Skia/Graphite shim (`src/Starling.Skia` + `native/`) was removed in
`wp:M5-skia-removal`; the engine paints exclusively via
`src/Starling.Paint/Backend/ImageSharpBackend.cs` (SixLabors.ImageSharp 4 +
ImageSharp.Drawing 3 + Fonts 3, pure-managed, requires the repo-root
`sixlabors.lic`). The default backend is the WebGPU compute-shader target
(equivalent to `STARLING_PAINT_BACKEND=imagesharp-webgpu`); set
`STARLING_PAINT_BACKEND=imagesharp` to opt back into the pure-CPU path.
There is no native graphics shim to build — a fresh checkout's `dotnet build`
should succeed without any non-.NET prerequisites.

## Coding standards — performance policy for C# code

Target modern .NET and use current platform primitives. Prefer simple,
allocation-conscious code. Do not introduce unsafe code, pooling, ref returns,
`stackalloc`, `ValueTask`, or `CollectionsMarshal` unless the code is plausibly
hot or allocation-heavy.

**Hot paths:**

- Avoid `Substring`; use `AsSpan`.
- Avoid `ToLower`/`ToUpper` for comparisons; use `StringComparison`.
- Avoid `ContainsKey` + indexer; use `TryGetValue`.
- Avoid `ContainsKey` + `Add`; use `TryAdd`.
- Avoid repeated `Regex` construction; use `[GeneratedRegex]`.
- Avoid reflection JSON serialization for known DTOs; use `System.Text.Json`
  source generation.
- Avoid repeated temporary arrays; use `static readonly` arrays, `ArrayPool<T>`,
  or `ArrayBufferWriter<T>`.
- Avoid LINQ chains in tight loops.
- Avoid closure allocations in loops; use `static` lambdas or explicit loops.
- Use `FrozenDictionary`/`FrozenSet` for build-once/read-many lookup tables.
- Use `SearchValues<T>` for repeated delimiter/character-set searches.
- Use `Span<T>`/`ReadOnlySpan<T>` for synchronous parsing and formatting.
- Use `Memory<T>`/`ReadOnlyMemory<T>` for async-friendly buffer APIs.
- Use `System.IO.Pipelines` for high-throughput streaming parsers.
- Enable and respect .NET performance analyzers.

Always preserve correctness, readability, cancellation behavior, and exception
semantics. If an optimization makes code meaningfully harder to understand, add
a short comment explaining the measured or expected benefit.

**.NET 11 / C# 15 preview additions:**

- For async-heavy hot paths, consider testing Runtime Async on `net11.0` with
  `<Features>runtime-async=on</Features>`. Measure throughput, allocation, stack
  traces, and library size before adopting broadly. Do not assume every async
  method becomes faster.
- When creating collections with collection expressions in C# 15, pass
  capacity/comparer arguments directly where useful:
  `List<T> items = [with(capacity: count), .. source];` and
  `HashSet<string> set = [with(StringComparer.OrdinalIgnoreCase), .. values];`.
- For process execution, prefer the new .NET 11 Process run-and-capture APIs
  over hand-wired `OutputDataReceived`/`ErrorDataReceived` code when launching
  child processes and capturing output.
- For compression code that already works on byte spans, prefer the new
  span-based Deflate, ZLib, and GZip encoder/decoder APIs instead of wrapping
  everything in `Stream` objects.
- For Base64 encoding/decoding in hot paths, prefer the new
  `System.Buffers.Text.Base64` span-based APIs over `Convert.ToBase64String` /
  `Convert.FromBase64String` when avoiding allocations matters.
- For UTF-8 / UTF-16 validators and parsers, use the new Utf8/Utf16 validation
  and invalid-subsequence APIs instead of hand-rolled validation loops.
- For reusable `Utf8JsonWriter` instances, use `Reset` overloads when
  appropriate instead of allocating a new writer every time.
- For bitset-style code using `BitArray`, use `BitArray.PopCount()` instead of
  manually counting true bits.
- For `MemoryCache`-heavy services, enable `MemoryCache` statistics and
  OpenTelemetry metrics when cache behavior affects performance decisions.
- Do not use C# 15 union types as a performance optimization. Use them for
  modeling correctness/exhaustiveness only, and treat them as preview until the
  language/runtime surface settles.

## Decision hierarchy when something's ambiguous

1. **The plan** (`browser-plan/`) — if a doc gives an answer, follow it.
2. **The work-package file** — it overrides general advice for the scope.
3. **The spec** the plan cites (WHATWG, ECMA, RFC) — follow the spec literally,
   citing section numbers in code comments.
4. **Ask** — open an `<!-- OPEN QUESTION -->` block in the relevant
   `browser-plan/` doc and proceed with your best guess. Do not block on
   absent humans.

## Concurrency model

- **Two agents on different packages:** independent. They commit to main
  in any order — different files, no conflict.
- **Two agents on the same package:** the second one to commit a `claim`
  edit loses the race (git non-fast-forward on the claim commit). They
  pull, see the new state, and pick a different package.
- **Agent dies mid-task:** the `claimed_at` timestamp shows when. After
  72 h with no commits referencing the package, any agent may release
  the claim and start over (or pick up using the handoff log).
- **Cross-package edits:** if your work changes a shared file
  (`Directory.Build.props`, `Directory.Packages.props`, `Starling.slnx`),
  call it out in the handoff log so a concurrent agent can rebase
  cleanly. These files are the merge-conflict hotspots.

## A worked example

Day 1 — agent-alex picks up `wp:M1-03-dom-core`:

```bash
./tasks/lib/claim.sh claim wp:M1-03-dom-core "agent-claude-alex"
# tasks/M1/wp-M1-03-dom-core.md now has status: claimed, claimed_by: agent-claude-alex
# ...writes Node.cs, NodeKind.cs, mutations on main...
git commit -am "wp:M1-03 — Node + NodeKind + first 30 mutation tests"
```

Day 2 — alex has to stop, riley picks it up:

```bash
# alex, end of session:
$EDITOR tasks/M1/wp-M1-03-dom-core.md   # adds handoff log entry
./tasks/lib/claim.sh release wp:M1-03-dom-core

# riley starts:
./tasks/lib/claim.sh claim wp:M1-03-dom-core "agent-claude-riley"
cat tasks/M1/wp-M1-03-dom-core.md       # reads the handoff log
# ...continues on main from "remaining: Element attribute mutations"...
```

Day 3 — riley finishes:

```bash
dotnet build && dotnet test             # green
git commit -am "wp:M1-03 — Element attribute mutations + tests"
./tasks/lib/claim.sh complete wp:M1-03-dom-core
# Update tasks/INDEX.md: M1-03 → 🟢 complete; M1-04, M1-06 unblock.
```

## Questions

If the design plan says X and reality says Y, edit the plan to match Y in
the same PR, and explain in the PR body. The plan is a living document —
it tracks the implementation, not the other way around.
