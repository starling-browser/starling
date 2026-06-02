# Starling

An independent web browser, built from scratch — no Chromium, Gecko, or WebKit. Written in .NET.

![Starling rendering netclaw.dev — an Astro + Redux-Toolkit site — in the Avalonia GUI shell](docs/screenshot.png)

<sub>netclaw.dev is shown only as a rendering target; Starling Browser has no direct affiliation with Netclaw.</sub>

## Why?

The conventional wisdom is that a browser engine has to be C++ or Rust, built by
a large team over many years. [Ladybird](https://ladybird.org) has already proven
the hardest part: that a genuinely independent engine is viable. Starling shares
that from-scratch spirit but asks the next question — do you still need a systems
language? Arc and Dia build their browsers in Swift, even on Windows, so the
C++-or-Rust assumption is already weaker than it looks. Starling pushes further:
a managed-first .NET engine, built in the open, independent of any browser
vendor.

The longer-term motivation is a specific frustration — how slowly the standards
process has delivered things like first-class WASM/WASI access to the DOM. The
plan is to earn parity on real-world sites first, then use the managed
architecture to make WASM a first-class citizen, hitting the DOM directly rather
than as a guest behind a JS bridge. And part of it, honestly, is just wanting to
find out whether it can be done.

[Read about our approach](/docs/approach.md)

## Project Status

_Last updated: 2026-05-25_

| Area | Status |
|---|---|
| [Overall](browser-plan/00_INDEX.md) | [![Experimental](https://img.shields.io/badge/status-experimental-red)](browser-plan/13_MILESTONES.md) |
| [Code coverage](https://codecov.io/gh/starling-browser/starling) | [![codecov](https://codecov.io/gh/starling-browser/starling/graph/badge.svg)](https://codecov.io/gh/starling-browser/starling) |
| [ECMAScript](browser-plan/09_JS_ENGINE.md) | [![Test262](https://img.shields.io/badge/Test262_language-95%25-brightgreen)](tests/Starling.Js.Test262.Tests/README.md) |
| [Web Platform](browser-plan/12_TESTING.md) | [![WPT](https://img.shields.io/badge/WPT-25%25-orange)](tests/Starling.Wpt.Tests/README.md) |
| [DOM](browser-plan/05_DOM.md) | [![DOM](https://img.shields.io/badge/DOM-partial-yellow)](tests/Starling.Dom.Tests/README.md) |
| [HTML Parser](browser-plan/04_HTML_PARSING.md) | [![HTML](https://img.shields.io/badge/HTML-spec_compliant-brightgreen)](tests/Starling.Html.Tests/README.md) |
| [CSS](browser-plan/06_CSS.md) | [![CSS](https://img.shields.io/badge/CSS-partial-yellow)](tests/Starling.Css.Spec.Tests/README.md) |
| [Layout](browser-plan/07_LAYOUT.md) | [![Layout](https://img.shields.io/badge/layout-partial-yellow)](tests/Starling.Layout.Tests/README.md) |
| [Paint](browser-plan/08_FONTS_PAINT.md) | [![Paint](https://img.shields.io/badge/paint-shipped-brightgreen)](tests/Starling.Paint.Tests/README.md) |
| [Networking](browser-plan/03_NETWORKING.md) | [![Networking](https://img.shields.io/badge/networking-partial-yellow)](tests/Starling.Net.Tests/README.md) |
| [JavaScript](browser-plan/09_JS_ENGINE.md) | [![JavaScript](https://img.shields.io/badge/JS-partial-yellow)](tests/Starling.Js.Tests/README.md) |
| [Web APIs / DOM bindings](browser-plan/10_WEB_APIS.md) | [![WebAPIs](https://img.shields.io/badge/Web_APIs-partial-yellow)](tests/Starling.Bindings.Tests/README.md) |
| [GUI shell](browser-plan/11_AVALONIA_SHELL.md) | [![GUI](https://img.shields.io/badge/GUI-partial-yellow)](tests/Starling.Gui.Tests/README.md) |
| [Multi-process / sandbox](browser-plan/01_ARCHITECTURE.md) | [![Sandbox](https://img.shields.io/badge/sandbox-not_started-lightgrey)](browser-plan/13_MILESTONES.md) |
| [Security](browser-plan/03_NETWORKING.md) | [![Security](https://img.shields.io/badge/security-not_hardened-red)](browser-plan/01_ARCHITECTURE.md) |

Each area name links to its design doc. Each badge links to the tests
that back the number. See
[`browser-plan/13_MILESTONES.md`](browser-plan/13_MILESTONES.md) for the
roadmap and [`tasks/INDEX.md`](tasks/INDEX.md) for the work-package queue.

## Quickstart

You'll need:

- The [.NET SDK 10.0.100](https://dotnet.microsoft.com/) or newer.
- A Six Labors license key for the paint backend — a **free community license**
  takes a couple of minutes. See [Six Labors license](#six-labors-license) below.

**Clone the repo with its submodules.** Starling pulls in the regular expression
engine from a sibling repository under `lib/`, so a plain `git clone` will leave
that directory empty and the build will fail. Use `--recurse-submodules`:

```bash
git clone --recurse-submodules https://github.com/starling-browser/starling.git
cd starling
```

If you already cloned without that flag, fetch the submodules now:

```bash
git submodule update --init --recursive
```

After a `git pull` that moves a submodule pointer, run the same command again to
sync the working tree.

**Build and test.**

```bash
dotnet restore
dotnet build
dotnet test
```

**Launch the browser.** `aspire run` brings up the full app — the Avalonia GUI
shell plus the headless renderer, orchestrated by Aspire (the dashboard URL
prints on stdout, typically <http://localhost:18888>). This is the desktop
browser pictured above.

```bash
aspire run
```

**Choosing engines / backends.** Pass selection flags after `aspire run --`:

```bash
# Defaults are --starling (JS VM) and --imagesharp-gpu (WebGPU paint).
aspire run -- --jint --imagesharp     # Jint JS engine + CPU paint backend
```

**Render a live site from the CLI.** The headless renderer fetches and paints a
real URL straight to a PNG:

```bash
# The built CLI binary is named `starling`.
dotnet run --project src/Starling.Headless -- render https://example.com -o example.png
```

This is exercised in CI, and real-world bundles render end-to-end (netclaw.dev,
pictured above). You can also point it at a local fixture — bare filesystem paths
auto-normalize to `file://`:

```bash
dotnet run --project src/Starling.Headless -- render testdata/hello.html -o out.png
```

The CLI's full shape is documented in [`browser-plan/02_PROJECT_SETUP.md`](browser-plan/02_PROJECT_SETUP.md#headless-cli-shape).

### Six Labors license

The engine paints via **ImageSharp.Drawing 3** — pure-managed, no native
graphics shim to build — which is commercially licensed by Six Labors. The repo
does **not** ship a license key (`sixlabors.lic` is gitignored), so each
contributor supplies their own. **Applying for a community license is quick and
easy: <https://licensing.sixlabors.com/>.** Save the key as `sixlabors.lic` in
the repository root and the build picks it up automatically (CI uses the
`SIXLABORS_LICENSE_KEY` secret instead).

## Working on Starling

Each subsystem has a focused doc in [`browser-plan/`](browser-plan/). For agent-facing work
packages with explicit inputs / outputs / acceptance, see
[`14_AGENT_TASKS.md`](browser-plan/14_AGENT_TASKS.md).

**New here?** Start with the design spec ([`browser-plan/00_INDEX.md`](browser-plan/00_INDEX.md)),
the engineering conventions in [`AGENTS.md`](AGENTS.md), and the contribution
rules in [`CONTRIBUTING.md`](CONTRIBUTING.md). Bug reports, questions, and
proposals are welcome via GitHub issues.

**Implementation agents:** start with [`AGENTS.md`](AGENTS.md) and the queue at
[`tasks/INDEX.md`](tasks/INDEX.md). Multiple agents can work in parallel — claim
an unblocked package via `./tasks/lib/claim.sh`, commit directly to `main` with
the wp id in the subject, leave a handoff-log entry on stop, and mark complete
via `./tasks/lib/claim.sh complete <wp-id>`. The full workflow is in
[`tasks/README.md`](tasks/README.md).

**Benchmarks:** how to run the suite and read the numbers is in
[`bench/README.md`](bench/README.md). The latest overview lives in
[`bench/benchmarks.md`](bench/benchmarks.md).

## License

Starling source code is licensed under [Apache-2.0](LICENSE) — Copyright 2026
Cody Mullins.

Third-party code, data, fonts, and fixtures may use different licenses. See
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). The current paint path also
requires a separate Six Labors license key to build. See
[Six Labors license](#six-labors-license).

The Starling name, logo, icons, and branding are not licensed with the source
code. See [`TRADEMARKS.md`](TRADEMARKS.md).
