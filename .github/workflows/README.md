# Workflows are disabled on purpose

This repository builds and publishes its binaries **manually** from a tagged
commit, not through GitHub Actions.

Rationale:

- The Windows MSI is built with the WiX toolset and the native launcher with
  Go. Both need a Windows toolchain, and the produced package is signed and
  published by hand.
- Every release is therefore a deliberate, reproducible step:
  `node packaging/build-msi.mjs` followed by `gh release create`.
- No runner ever executes code from a pull request, which removes a whole
  class of supply-chain risk from the build path.

To cut a release:

```bash
npm test                       # the full suite must pass
node packaging/build-msi.mjs   # produces build/agbrowser-<version>-x86.msi
git tag -a v0.1.0 -m "agbrowser 0.1.0"
git push origin v0.1.0
gh release create v0.1.0 build/agbrowser-0.1.0-x86.msi \
  --title "agbrowser 0.1.0" --notes "See the changelog."
```
