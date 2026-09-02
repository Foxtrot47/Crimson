# Installing and releasing Crimson

## Install a GitHub release

A tagged release contains these files:

- `Crimson-X.Y.Z-win-x64.zip` — portable application with a self-contained .NET runtime
- `Crimson-X.Y.Z-win-x64.msix` — signed sideload package
- `Crimson-X.Y.Z.cer` — public certificate for the sideload package
- `SHA256SUMS.txt` — SHA-256 checksums for the downloadable files
- `Crimson-X.Y.Z-partner-center.msixupload` — unsigned Microsoft Store submission; not intended for direct installation

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

## Publish a release

Pushing a canonical `vX.Y.Z` tag whose commit is on `origin/main` runs the release workflow. The workflow maps the tag to MSIX version `X.Y.Z.0`, runs both test suites, builds every release format, validates the package identity and signatures, and publishes the five files listed above.

The protected `release` GitHub environment must define:

- `MSIX_PFX_BASE64` — base64-encoded persistent code-signing PFX
- `MSIX_PFX_PASSWORD` — the PFX password

The certificate subject must be exactly `CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F` and it must contain the Code Signing EKU. Reuse the same certificate for every GitHub release so existing installations retain a stable trust chain. Keep an encrypted recovery copy outside GitHub; secret values cannot be retrieved from GitHub after upload.

Protect the `release` environment with required reviewers. Protect release tags and do not allow tags to be moved or reused.

### Create the signing secrets

Run this once on a controlled Windows workstation:

```powershell
$publisher = 'CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F'
$password = Read-Host 'PFX password' -AsSecureString
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject $publisher `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -KeyAlgorithm RSA `
  -KeyLength 3072 `
  -HashAlgorithm SHA256 `
  -KeyExportPolicy Exportable `
  -NotAfter (Get-Date).AddYears(5)

$pfxPath = Join-Path $env:TEMP 'Crimson-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath)) |
  gh secret set MSIX_PFX_BASE64 --env release

$plainPassword = [Net.NetworkCredential]::new('', $password).Password
$plainPassword | gh secret set MSIX_PFX_PASSWORD --env release
$plainPassword = $null
Remove-Item $pfxPath
```

The workflow imports the PFX only for the signing job, timestamps and verifies the public MSIX, exports the matching CER, and removes imported certificates in an unconditional cleanup block. Partner Center receives an unsigned `.msixupload` for Microsoft to sign.
