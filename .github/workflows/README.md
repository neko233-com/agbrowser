# Workflows are disabled on purpose

This repository builds and publishes its binaries **manually** from a tagged
commit, not through GitHub Actions.

Rationale:

- The Windows MSI is built with the WiX 5 and the .NET desktop application. Both need a Windows toolchain, and the produced package is signed and
  published by hand.
- Every release is therefore a deliberate, reproducible step:
  `node packaging/build-msi.mjs` followed by `gh release create`.
- No runner ever executes code from a pull request, which removes a whole
  class of supply-chain risk from the build path.

To cut a release:

```powershell
$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
npm test
npm run build:msi
git tag -a "v$version" -m "agbrowser $version"
git push origin "v$version"
gh release create "v$version" "build/agbrowser-$version-x64.msi" `
  --title "agbrowser $version" --notes "See the changelog."
```

The desktop updater only offers a stable release when its tag is `vX.Y.Z`,
the release contains `agbrowser-X.Y.Z-x64.msi`, and GitHub exposes that
asset's SHA-256 digest. The older x86 launcher asset is not a desktop update.
