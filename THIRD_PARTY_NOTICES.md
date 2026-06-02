# Third-Party Notices

Starling source code is licensed under Apache-2.0 unless a file or directory
states a different license.

This file tracks third-party code, data, and assets that ship in this repo or
are required by the current build.

## Starling.RegExp

Path: `lib/starling-regexp/`

Source: <https://github.com/starling-browser/starling-regexp>

License: Apache-2.0. See `lib/starling-regexp/LICENSE`.

`Starling.RegExp` is a Git submodule with its own license and package metadata.

## Six Labors Stack

Packages:

- `SixLabors.ImageSharp`
- `SixLabors.ImageSharp.Drawing`
- `SixLabors.ImageSharp.Drawing.WebGPU`
- `SixLabors.Fonts`

License: Six Labors Split License, Version 1.0, plus any commercial terms that
apply to your use.

The current paint path directly references these packages. Building Starling
requires a valid Six Labors license key. The repo does not ship that key.

## Geist Fonts

Path: `src/Starling.Gui/Assets/Fonts/`

Source: <https://github.com/vercel/geist-font>

License: SIL Open Font License 1.1. See
`src/Starling.Gui/Assets/Fonts/OFL.txt`.

## Open Sans

Path: `src/Starling.Paint/Resources/Fonts/OpenSans-Regular.ttf`

Source: <https://fonts.google.com/specimen/Open+Sans>

License: Apache-2.0.

## W3C Webref Data

Path: `testdata/webref/`

Source: <https://github.com/w3c/webref>

License: MIT. See `testdata/webref/README.md` for the pinned snapshot.

## html5lib Tests

Path: `testdata/spec/html5lib-tests/`

Source: <https://github.com/html5lib/html5lib-tests>

License: MIT.

## Web Platform Tests Encoding Fixtures

Path: `testdata/wpt/encoding/`

Sources:

- <https://github.com/web-platform-tests/wpt/tree/master/encoding>
- <https://encoding.spec.whatwg.org/>

License: BSD-3-Clause for upstream Web Platform Tests material. The WHATWG
Encoding Standard has its own terms for standard text and indexes.

## Public Suffix List

Path: `src/Starling.Net/Resources/Psl/effective_tld_names.dat`

Source: <https://publicsuffix.org/>

License: Mozilla Public License 2.0. The file carries its upstream license
notice.

## Site Snapshots

Path: `testdata/snapshots/nginx.org/`

Source: <https://nginx.org/>

These fixtures are pinned snapshots used for rendering tests. The captured page
links to the upstream 2-clause BSD license for nginx source. Logos and site
branding may have separate trademark terms.
