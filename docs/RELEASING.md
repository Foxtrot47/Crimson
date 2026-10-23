# Installing and releasing Crimson

## Install a GitHub release

A release contains these files:

- `Crimson-X.Y.Z-win-x64.zip`: portable application with a self-contained .NET runtime
- `Crimson-X.Y.Z-partner-center.msixupload`: unsigned Microsoft Store submission; not intended for direct installation
- `SHA256SUMS.txt`: SHA-256 checksums for the downloadable files

CI publishes unsigned assets only. Signing stays on a workstation that holds the code-signing key, so a release can be produced without any signing secret.

### Portable ZIP

Download and extract `Crimson-X.Y.Z-win-x64.zip`, then run `Crimson.exe`. The portable build does not require certificate installation.

The portable build requires the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section) for Epic sign-in and Store access. It is already present on most current Windows installations but is not bundled in the portable ZIP.

### Microsoft Store package

`Crimson-X.Y.Z-partner-center.msixupload` is the unsigned submission container for Partner Center, so Windows cannot install it directly. Install Crimson from the Microsoft Store, or sideload a locally signed MSIX built with the steps below.

The Store package and a sideloaded MSIX share the same package identity, so they cannot be installed side by side. They also share Windows-managed package data. The portable build instead uses `%LOCALAPPDATA%\Crimson` directly.

### Verify downloads

Compare a downloaded file against its entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Crimson-X.Y.Z-win-x64.zip -Algorithm SHA256
Get-FileHash .\Crimson-X.Y.Z-partner-center.msixupload -Algorithm SHA256
```

## License and corresponding source

Binary distributions include the repository's `LICENSE.txt`. Release validation checks that the portable output and both MSIX variants contain an identical copy.

Publish the source matching each distributed binary, including build scripts and required dependency information. For release `X.Y.Z`, link to `https://github.com/Foxtrot47/Crimson/tree/vX.Y.Z` next to the binary downloads and in the Microsoft Store listing. Verify that the tag is public and identifies the source actually used to build that package. Do not publish packages built from uncommitted changes under an unrelated source tag.

## Store display name

The reserved Partner Center name is **Crimson Launcher**. Both `Package/Properties/DisplayName` and the application's `uap:VisualElements/@DisplayName` must use that exact name. Windows may show it in Start and installed-app listings. The title bar, tray, and in-app UI keep the **Crimson** branding.

This does not change the package identity `Foxtrot47.CrimsonLauncher`, publisher, signing certificate, or Store ID `9P6MZFN90NN8`. Release validation checks the source and generated package display names against this confirmed reservation; it does not query Partner Center.

## Versioning

The first stable release is `1.0.0`, with MSIX version `1.0.0.0`. `Directory.Build.props` defines the product, assembly, file, and default package versions. Keep `Package.appxmanifest` synchronized with the product version and leave the fourth MSIX component at zero.

For future releases, bump the version in source before creating the matching `vX.Y.Z` tag. The release build rejects a version that does not match `Directory.Build.props`.

## Build locally with an existing signing key

Close Crimson first. The build clears generated `Crimson.WinUI/bin` and `Crimson.WinUI/obj` between package variants; it does not install or launch the application. Existing release directories are not overwritten.

Use the existing code-signing certificate in the current user's Personal certificate store. This does not generate a new certificate or export its private key:

```powershell
./scripts/build-release.ps1 -Version 1.0.0 -MsixVersion 1.0.0.0 `
  -CertificateThumbprint '<existing certificate thumbprint>' `
  -OutputDirectory './dist/1.0.0' -SkipCertificateTrust
```

`-SkipCertificateTrust` leaves certificate trust stores unchanged. Signature verification must succeed using the workstation's existing trust configuration. Without that switch, the pipeline temporarily trusts the public certificate and removes only trust added by that build afterward. The exported `.cer` contains the same public certificate, never the private key.

## Publish a release

Bump the version in `Directory.Build.props` and `Package.appxmanifest` on `main`, then dispatch the release workflow from `main`. It rejects a version whose `vX.Y.Z` tag or GitHub Release already exists, maps the version to MSIX version `X.Y.Z.0`, runs both test suites, builds the assets, verifies package identity, creates the tag, and publishes the release.

No signing secret is required. The workflow calls `./scripts/build-release.ps1 -Unsigned`, which skips the certificate import, signature verification, and `.cer` export.

Protect the `release` environment with required reviewers. Protect release tags and do not allow tags to be moved or reused.

### Preserve the existing signing key

Routine releases must reuse the existing signing certificate and private key. Do not generate a replacement certificate or rotate the key while preparing a release: doing so changes the trust chain for existing installations.

Local signing uses the current user's existing Personal-store certificate by thumbprint. `build-release.ps1` accepts either that thumbprint or the `MSIX_PFX_BASE64` and `MSIX_PFX_PASSWORD` secrets for a workstation that has no imported key.

A signing build imports the PFX only for that run, timestamps and verifies the public MSIX, exports the matching public CER, and removes only certificates imported by that build in an unconditional cleanup block. Partner Center receives an unsigned `.msixupload` for Microsoft to sign.

The certificate subject must be exactly `CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F` and it must contain the Code Signing EKU. Reuse the same certificate for every signed MSIX so existing installations retain a stable trust chain, and keep an encrypted recovery copy outside GitHub. Never print secret values or commit signing material to the repository.
