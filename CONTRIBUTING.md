# Contributing

This project accepts human and AI-assisted contributions. The contributor is
responsible for the change either way.

## Contributor License Agreement

Contributions require a signed Contributor License Agreement. See [`CLA.md`](CLA.md).

CLA Assistant checks pull requests and asks contributors to sign when needed.

Direct commits to `main` should only be made by people who already have a CLA on
file. Bots should be listed in the CLA Assistant allowlist.

## AI-Assisted Work

AI-assisted contributions are allowed when the contributor has reviewed,
understood, tested, and can explain the change.

By submitting AI-assisted code, you certify that:

- you did not knowingly copy code from an incompatible source
- you reviewed the output for license, security, and correctness issues
- you are responsible for the contribution under the CLA
- generated ports, translated code, parser tables, large generated files, and
  copied algorithms are disclosed in the PR or handoff log

Small autocomplete-style suggestions do not need a disclosure.

## License And Notices

Owned Starling source code is licensed under Apache-2.0. Third-party code, data,
fonts, and fixtures may use different licenses. See `THIRD_PARTY_NOTICES.md`.

Add an SPDX header to new owned C# files:

```csharp
// SPDX-License-Identifier: Apache-2.0
```

Do not add Starling SPDX headers to vendored data, generated files, submodules,
or third-party assets unless that upstream source already uses the same header.

## Trademarks

The Starling name, logo, icons, and branding are not licensed with the source
code. See `TRADEMARKS.md`.
