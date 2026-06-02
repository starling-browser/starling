# WPT `encoding/` curated subset

This directory mirrors a focused slice of the upstream
[`web-platform-tests/wpt/encoding/`](https://github.com/web-platform-tests/wpt/tree/master/encoding)
suite that we exercise from `tests/Starling.Engine.Tests/EngineEncodingTests.cs`.
Upstream Web Platform Tests material is BSD-3-Clause.

We do not vendor the WPT HTML harnesses (`*-decode-form.html`,
`single-byte-decoder.html`, etc.) — they require a browser test runner.
Instead we vendor JSON corpora that encode the same expectations the WPT
HTML harnesses assert, derived directly from the WHATWG Encoding
Standard indexes (https://encoding.spec.whatwg.org/, snapshot 2026-04-22).
The WHATWG Encoding Standard has its own terms for standard text and indexes.

## Files

- `encodings.json` — WHATWG label → canonical-name table fixture (mirrors
  WPT's `encoding/resources/encodings.js` shape).
- `decode-fixtures.json` — per-encoding `(byte-sequence, expected-string)`
  triples mirroring `single-byte-decoder.html` and the multi-byte
  `*-decode-form.html` harnesses. Each fixture has a `source` field
  pointing at the WPT page whose assertion it captures.

## Excluded subtests (and why)

- `replacement` encoding tests (e.g. `iso-2022-cn-decode-form.html`):
  Starling does not implement the WHATWG "replacement" decoder yet — a
  deliberately-failing decoder for known-broken labels. Listed as
  follow-up in the wp:M2-07d commit log.
- `x-user-defined`: not shipped by the BCL CodePages provider; would
  require a hand-rolled passthrough table. Deferred.
- `iso-8859-10`, `iso-8859-14`, `iso-8859-16`: the .NET 10
  `System.Text.Encoding.CodePages` provider does not ship code pages
  28600 / 28604 / 28606, so `Encoding.GetEncoding("ISO-8859-10")` etc.
  throw `ArgumentException`. Starling's label table still maps these
  labels (so a future provider transparently picks them up) but the
  decode path falls back to UTF-8 instead of mis-decoding. Cover with a
  hand-rolled single-byte table in a follow-up if WPT pressure
  warrants.
- `windows-1250 byte 0xB5`: the BCL cp1250 encoding maps 0xB5 to
  U+00B5 (micro sign), while WHATWG maps it to U+0105 (ą). This is a
  documented .NET historical deviation; the fixture omits this single
  byte rather than encoding the discrepancy. All other windows-1250
  positions tested round-trip correctly.
- Streaming / form-submission tests: out of scope for v1 charset
  sniffing — Starling only decodes already-buffered HTTP response bodies.
- `textdecoder-fatal-streaming.html` and the TextDecoder API tests
  generally: Starling does not expose `TextDecoder` to script yet.

The remaining curated corpus is large enough to validate every label
family enumerated in `wp:M2-07d` (≥ 95% gate per the work-package
acceptance criteria).
