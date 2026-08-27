# Releasing Crimson

Pushing a canonical `vX.Y.Z` tag whose commit is on `origin/main` runs the release workflow. The workflow maps the tag to MSIX version `X.Y.Z.0`, tests the tagged source, and publishes these GitHub Release assets:

- `Crimson-X.Y.Z-win-x64.zip` — portable, self-contained build
- `Crimson-X.Y.Z-win-x64.msix` — self-signed sideload package
- `Crimson-X.Y.Z.cer` — public certificate for the sideload package
- `Crimson-X.Y.Z-partner-center.msixupload` — unsigned Microsoft Store submission
- `SHA256SUMS.txt`

The `release` GitHub environment must define:

- `MSIX_PFX_BASE64` — base64-encoded persistent code-signing PFX
- `MSIX_PFX_PASSWORD` — the PFX password

The certificate subject must be exactly `CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F` and it must contain the Code Signing EKU. Reuse the same certificate for every GitHub release so existing installations retain a stable trust chain. Keep an encrypted recovery copy outside GitHub; secret values cannot be retrieved from GitHub after upload.

Protect the `release` environment with required reviewers. Protect release tags and do not allow tags to be moved or reused.

## Create the signing secret

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

The workflow imports the PFX only for the signing job, timestamps and verifies the public MSIX, exports the matching CER, and removes imported certificates in an unconditional cleanup block. The Partner Center package remains unsigned for Microsoft to sign.

## Sideloading note

Users must import the released CER into the current user's **Trusted People** store before installing the MSIX. Crimson is a full-trust desktop application. The sideload and Microsoft Store packages share the same package identity and cannot be installed side by side. Both use the existing `%LOCALAPPDATA%\Crimson\` application data.
