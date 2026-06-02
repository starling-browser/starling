# Upstream pin

Source: https://github.com/html5lib/html5lib-tests
Commit: `e4463205ac3c4500e1379103daadfdcfe5e33af5` (2026-05-05)
Path:   `tree-construction/`
License: MIT

The runner that consumes these fixtures lives at
`tests/Starling.Html.Tests/TreeBuilder/Html5LibTreeConstructionTests.cs`
(wp:M1-02b). Re-vendor by re-running:

```bash
git clone --depth=1 https://github.com/html5lib/html5lib-tests.git /tmp/h5l
cp /tmp/h5l/tree-construction/*.dat                testdata/spec/html5lib-tests/tree-construction/
cp /tmp/h5l/tree-construction/scripted/*.dat       testdata/spec/html5lib-tests/tree-construction/scripted/
cp /tmp/h5l/tree-construction/README.md            testdata/spec/html5lib-tests/tree-construction/
```

Update this file's commit pin in the same change.

Format spec: see `README.md` next to this file.
