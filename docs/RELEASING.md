# Installing and releasing Crimson

## Install a GitHub release

A tagged release contains these files:

- `Crimson-X.Y.Z-win-x64.zip`: portable application with a self-contained .NET runtime
- `Crimson-X.Y.Z-win-x64.msix`: signed sideload package
- `Crimson-X.Y.Z.cer`: public certificate for the sideload package
- `SHA256SUMS.txt`: SHA-256 checksums for the downloadable files
- `Crimson-X.Y.Z-partner-center.msixupload`: unsigned Microsoft Store submission; not intended for direct installation

### Portable ZIP

Download and extract `Crimson-X.Y.Z-win-x64.zip`, then run `Crimson.exe`. The portable build does not require certificate installation.

Both installation formats require the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section) for Epic sign-in and Store access. It is already present on most current Windows installations but is not bundled in the portable ZIP.

### Sideloaded MSIX

The GitHub MSIX uses a persistent self-signed certificate. Windows must trust the matching public certificate before it will install the package.

1. Download the `.msix` and `.cer` files for the same release.
2. Open the `.cer` file and select **Install Certificate**.
3. Select **Current User**.
4. Choose **Place all certificates in the following store** and select **Trusted People**.
5. Complete the certificate import, then open the `.msix` file.

Only install certificates obtained from the official Crimson repository. Remove the certificate from the current user's **Trusted People** store if you no longer want to trust future packages signed with it.

The sideload and Microsoft Store packages share the same package identity, so they cannot be installed side by side. They also share Windows-managed package data. The portable build instead uses `%LOCALAPPDATA%\Crimson` directly.

### Verify downloads

Compare a downloaded file against its entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\Crimson-X.Y.Z-win-x64.zip -Algorithm SHA256
Get-FileHash .\Crimson-X.Y.Z-win-x64.msix -Algorithm SHA256
```

## License and corresponding source

Binary distributions include the repository's `LICENSE.txt`. Release validation checks that the portable output and both MSIX variants contain an identical copy.

Publish the source matching each distributed binary, including build scripts and required dependency information. For release `X.Y.Z`, link to `https://github.com/Foxtrot47/Crimson/tree/vX.Y.Z` next to the binary downloads and in the Microsoft Store listing. Verify that the tag is public and identifies the source actually used to build that package. Do not publish packages built from uncommitted changes under an unrelated source tag.

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

`-SkipCertificateTrust` leaves certificate trust stores unchanged. Signature verification must succeed using the workstation's existing trust configuration. Without that switch, the pipeline temporarily trusts the public certificate and removes only trust added by that build afterward. The exported `.cer` contains the same public certificate, never the private key. CI can continue importing the existing encrypted PFX from its protected secrets.

## Publish a release

Pushing a canonical `vX.Y.Z` tag whose commit is on `origin/main` runs the release workflow. The workflow maps the tag to MSIX version `X.Y.Z.0`, runs both test suites, builds every release format, validates the package identity and signatures, and publishes the five files listed above.

The protected `release` GitHub environment must define:

- `MSIX_PFX_BASE64`: base64-encoded persistent code-signing PFX
- `MSIX_PFX_PASSWORD`: the PFX password

The certificate subject must be exactly `CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F` and it must contain the Code Signing EKU. Reuse the same certificate for every GitHub release so existing installations retain a stable trust chain. Keep an encrypted recovery copy outside GitHub; secret values cannot be retrieved from GitHub after upload.

Protect the `release` environment with required reviewers. Protect release tags and do not allow tags to be moved or reused.

### Preserve the existing signing key

Routine releases must reuse the existing signing certificate and private key. Do not generate a replacement certificate or rotate the key while preparing a release: doing so changes the trust chain for existing installations.

Local builds use the current user's existing Personal-store certificate by thumbprint. CI reuses the existing protected `MSIX_PFX_BASE64` and `MSIX_PFX_PASSWORD` secrets. If those secrets need recovery, restore them from the existing encrypted recovery PFX on a trusted workstation. Never print secret values or commit signing material to the repository. Any key rotation requires separate approval and an installation-trust migration plan.

The workflow imports the existing PFX only for the signing job, timestamps and verifies the public MSIX, exports the matching public CER, and removes only certificates imported by that build in an unconditional cleanup block. Partner Center receives an unsigned `.msixupload` for Microsoft to sign.
