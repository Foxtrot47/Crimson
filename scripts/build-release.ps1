[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
  [string]$Version,

  [Parameter(Mandatory)]
  [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.0$')]
  [string]$MsixVersion,

  [ValidatePattern('^[0-9a-fA-F]{40}$')]
  [string]$CertificateThumbprint,

  [string]$OutputDirectory,

  [switch]$SkipCertificateTrust,

  [switch]$Unsigned
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Crimson.WinUI\Crimson.WinUI.csproj'
$manifestPath = Join-Path $repo 'Crimson.WinUI\Package.appxmanifest'
$dist = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) } else { Join-Path $repo 'dist' }
$staging = Join-Path $repo ('artifacts\release-staging-' + [guid]::NewGuid().ToString('N'))
$signedOutput = Join-Path $staging 'msix-test'
$storeOutput = Join-Path $staging 'msix'
$publisher = 'CN=B3628FD3-BCE4-4EF1-ADE8-7B0F73A4FC3F'
$packageName = 'Foxtrot47.CrimsonLauncher'
$displayName = 'Crimson Launcher'
$temporaryDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfxPath = Join-Path $temporaryDirectory ('Crimson-signing-' + [guid]::NewGuid().ToString('N') + '.pfx')
$originalManifest = [IO.File]::ReadAllBytes($manifestPath)
$signingThumbprint = $null
$importedCertificateThumbprints = @()
$trustedRootThumbprint = $null
$removeTrustedRootCertificate = $false

function Invoke-Checked {
  param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string[]]$ArgumentList
  )

  & $FilePath @ArgumentList | Out-Host
  if ($LASTEXITCODE -ne 0) {
    throw "$FilePath failed with exit code $LASTEXITCODE."
  }
}

function Find-WindowsSdkTool {
  param([Parameter(Mandatory)][string]$Name)

  $root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $tool = Get-ChildItem $root -Recurse -Filter $Name -File |
    Where-Object { $_.DirectoryName -match '\\x64$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
  if (-not $tool) { throw "Could not find $Name in the Windows SDK." }
  return $tool.FullName
}

function Clear-ProjectOutput {
  if (Get-Process -Name 'Crimson' -ErrorAction SilentlyContinue) {
    throw 'Close Crimson before building release packages. No running process was stopped.'
  }
  Remove-Item (Join-Path $repo 'Crimson.WinUI\bin'), (Join-Path $repo 'Crimson.WinUI\obj') -Recurse -Force -ErrorAction SilentlyContinue
}

function Assert-ReleaseInputs {
  if (-not $Unsigned -and -not $CertificateThumbprint -and
      ([string]::IsNullOrWhiteSpace($env:MSIX_PFX_BASE64) -or
       [string]::IsNullOrWhiteSpace($env:MSIX_PFX_PASSWORD))) {
    throw 'MSIX_PFX_BASE64 and MSIX_PFX_PASSWORD must be configured in the release environment.'
  }

  $versionComponents = $MsixVersion.Split('.') | ForEach-Object { [int64]$_ }
  if ($versionComponents.Count -ne 4 -or
      $versionComponents[3] -ne 0 -or
      ($versionComponents | Where-Object { $_ -gt 65535 })) {
    throw "Invalid MSIX version '$MsixVersion'."
  }
  if ($MsixVersion -cne "$Version.0") {
    throw "MSIX version '$MsixVersion' does not correspond to product version '$Version'."
  }
  Assert-SourceVersions
}

function Assert-SourceVersions {
  [xml]$versionProperties = [IO.File]::ReadAllText((Join-Path $repo 'Directory.Build.props'))
  if ($versionProperties.Project.PropertyGroup.Version -cne $Version) {
    throw 'The requested release version does not match Directory.Build.props.'
  }
  [xml]$sourceManifest = [IO.File]::ReadAllText($manifestPath)
  if ($sourceManifest.Package.Identity.Version -cne $MsixVersion) {
    throw 'The requested MSIX version does not match Package.appxmanifest.'
  }
  Assert-ReservedDisplayNames $sourceManifest
}

function Assert-ReservedDisplayNames {
  param([Parameter(Mandatory)][xml]$Manifest)

  if ($Manifest.Package.Properties.DisplayName -cne $displayName -or
      $Manifest.Package.Applications.Application.VisualElements.DisplayName -cne $displayName) {
    throw 'Package and application display names must match the reserved Store name Crimson Launcher.'
  }
}

function Initialize-ReleaseDirectories {
  foreach ($directory in @($dist, $staging)) {
    if (Test-Path $directory) { throw "Release output already exists and will not be overwritten: $directory" }
  }
  New-Item $dist, $staging -ItemType Directory | Out-Null
}

function Set-PackageVersion {
  [xml]$packageManifest = [IO.File]::ReadAllText($manifestPath)
  $identity = $packageManifest.Package.Identity
  if ($identity.Name -cne $packageName -or $identity.Publisher -cne $publisher) {
    throw 'Package.appxmanifest does not contain the expected Partner Center identity.'
  }

  $identity.SetAttribute('Version', $MsixVersion)
  $writerSettings = [Xml.XmlWriterSettings]::new()
  $writerSettings.Encoding = [Text.UTF8Encoding]::new($true)
  $writerSettings.Indent = $true
  $writer = [Xml.XmlWriter]::Create($manifestPath, $writerSettings)
  try { $packageManifest.Save($writer) } finally { $writer.Dispose() }
}

# Windows App SDK 1.8 bundles its Windows AI (Phi Silica) and Windows ML (ONNX Runtime)
# payload into every output via the monolithic WindowsAppRuntime MSIX (~25 MB, including a
# 21 MB native onnxruntime.dll). Crimson never calls those APIs, and MSBuild-side exclusions
# cannot filter the extracted MSIX payload, so the files are deleted during packaging instead.
$script:BloatPatterns = @('onnxruntime*.dll', 'Microsoft.ML.OnnxRuntime*.dll', 'Microsoft.Windows.AI.*')

function Remove-BloatFromDirectory {
  param([Parameter(Mandatory)][string]$Path)

  $removedBytes = 0L
  $removedCount = 0
  foreach ($pattern in $script:BloatPatterns) {
    foreach ($file in Get-ChildItem $Path -Recurse -Filter $pattern -File) {
      $removedBytes += $file.Length
      $removedCount++
      Remove-Item $file.FullName -Force
    }
  }
  Write-Host ("Removed {0} unused Windows AI/ML payload file(s), {1:N1} MB." -f $removedCount, ($removedBytes / 1MB))
  foreach ($pattern in $script:BloatPatterns) {
    if (Get-ChildItem $Path -Recurse -Filter $pattern -File) {
      throw "Bloat removal failed: files matching '$pattern' are still present under $Path."
    }
  }
}

function Remove-BloatFromMsix {
  param([Parameter(Mandatory)][string]$MsixPath)

  $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
  $work = Join-Path $staging ('msix-debloat-' + [guid]::NewGuid().ToString('N'))
  try {
    Invoke-Checked $makeAppx @('unpack', '/p', $MsixPath, '/d', $work, '/o') | Out-Null
    Remove-BloatFromDirectory $work
    # Pack to a temporary path first so a failure never destroys the only copy of the asset.
    # Repacking drops any existing signature; the caller signs the result afterwards.
    $repacked = Join-Path $staging ('msix-repack-' + [guid]::NewGuid().ToString('N') + '.msix')
    Invoke-Checked $makeAppx @('pack', '/d', $work, '/p', $repacked) | Out-Null
    Move-Item $repacked $MsixPath -Force
  }
  finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Remove-BloatFromMsixUpload {
  param([Parameter(Mandatory)][string]$UploadPath)

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $work = Join-Path $staging ('msixupload-debloat-' + [guid]::NewGuid().ToString('N'))
  try {
    [IO.Compression.ZipFile]::ExtractToDirectory($UploadPath, $work)
    $innerPackages = @(Get-ChildItem $work -Recurse -Filter *.msix -File)
    if ($innerPackages.Count -ne 1) {
      throw "Expected one MSIX inside the Store upload, found $($innerPackages.Count)."
    }
    Remove-BloatFromMsix $innerPackages[0].FullName
    $repacked = $UploadPath + '.repack'
    # ZipFile round-trips nested entry separators faithfully; Compress-Archive does not.
    [IO.Compression.ZipFile]::CreateFromDirectory($work, $repacked)
    Remove-Item $UploadPath -Force
    Move-Item $repacked $UploadPath
  }
  finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
  }
}

function Invoke-ReleaseTests {
  Invoke-Checked 'dotnet' @(
    'test', (Join-Path $repo 'Crimson.Core.Tests\Crimson.Core.Tests.csproj'),
    '-c', 'Release', '--nologo', '-v', 'minimal'
  )
  Invoke-Checked 'dotnet' @(
    'restore', (Join-Path $repo 'Crimson.sln'),
    '-r', 'win-x64', '-p:Platform=x64', '-p:PublishReadyToRun=true',
    '--nologo', '-v', 'minimal'
  )
  Invoke-Checked 'dotnet' @(
    'test', (Join-Path $repo 'Crimson.Tests\Crimson.Tests.csproj'),
    '--no-restore', '-c', 'Release', '-p:Platform=x64',
    '--nologo', '-v', 'minimal'
  )
}

function Assert-LicenseFile {
  param([Parameter(Mandatory)][string]$Directory)

  $licensePath = Join-Path $Directory 'LICENSE.txt'
  if (-not (Test-Path $licensePath -PathType Leaf)) {
    throw "Release output is missing LICENSE.txt: $Directory"
  }
  $expected = (Get-FileHash (Join-Path $repo 'LICENSE.txt') -Algorithm SHA256).Hash
  if ((Get-FileHash $licensePath -Algorithm SHA256).Hash -cne $expected) {
    throw "Release output contains a different LICENSE.txt: $Directory"
  }
}

function Publish-PortableAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'publish', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '--self-contained', 'true',
    "-p:Version=$Version", "-p:InformationalVersion=$Version",
    '-p:PublishReadyToRun=true',
    '-o', (Join-Path $staging 'unpackaged'),
    '--nologo', '-v', 'minimal'
  )

  $portableExe = Join-Path $staging 'unpackaged\Crimson.exe'
  if (-not (Test-Path $portableExe)) { throw 'Portable publish did not produce Crimson.exe.' }
  Assert-LicenseFile (Join-Path $staging 'unpackaged')
  Remove-BloatFromDirectory (Join-Path $staging 'unpackaged')
  Compress-Archive `
    -Path (Join-Path $staging 'unpackaged\*') `
    -DestinationPath (Join-Path $dist "Crimson-$Version-win-x64.zip")
}

function Read-SigningPfx {
  try {
    return [Convert]::FromBase64String($env:MSIX_PFX_BASE64)
  }
  catch {
    throw 'MSIX_PFX_BASE64 is not valid base64.'
  }
}

function Assert-SigningCertificate {
  param([Parameter(Mandatory)]$Certificate)

  if (-not $Certificate.HasPrivateKey) {
    throw 'The imported signing certificate does not contain a private key.'
  }
  if ($Certificate.Subject -cne $publisher) {
    throw "Signing certificate subject '$($Certificate.Subject)' does not match '$publisher'."
  }

  $now = Get-Date
  if ($Certificate.NotBefore -gt $now -or $Certificate.NotAfter -le $now) {
    throw 'The signing certificate is not currently valid.'
  }
  $codeSigningEku = $Certificate.EnhancedKeyUsageList |
    Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }
  if (-not $codeSigningEku) {
    throw 'The signing certificate does not permit code signing.'
  }
}

function Import-SigningCertificate {
  $pfxBytes = Read-SigningPfx
  [IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
  [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)

  $securePassword = ConvertTo-SecureString $env:MSIX_PFX_PASSWORD -AsPlainText -Force
  $previewCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $pfxPath,
    $env:MSIX_PFX_PASSWORD,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
  $script:signingThumbprint = $previewCertificate.Thumbprint
  $existingCertificateThumbprints = @(Get-ChildItem 'Cert:\CurrentUser\My' | Select-Object -ExpandProperty Thumbprint)
  $previewCertificate.Dispose()

  $imported = Import-PfxCertificate `
    -FilePath $pfxPath `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -Password $securePassword
  $script:importedCertificateThumbprints = @($imported |
    Where-Object { $_.Thumbprint -notin $existingCertificateThumbprints } |
    Select-Object -ExpandProperty Thumbprint)
  Remove-Item $pfxPath -Force

  $signingCertificate = $imported |
    Where-Object { $_.Thumbprint -eq $signingThumbprint } |
    Select-Object -First 1
  Assert-SigningCertificate $signingCertificate
  return $signingCertificate
}

function Get-ReleaseSigningCertificate {
  if (-not $CertificateThumbprint) { return Import-SigningCertificate }
  $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
  Assert-SigningCertificate $certificate
  $script:signingThumbprint = $certificate.Thumbprint
  return $certificate
}

function Export-PublicCertificate {
  param([Parameter(Mandatory)]$Certificate)

  $cerPath = Join-Path $dist "Crimson-$Version.cer"
  Export-Certificate -Cert $Certificate -FilePath $cerPath -Type CERT | Out-Null
  $exportedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
  try {
    if ($exportedCertificate.Thumbprint -ne $signingThumbprint -or $exportedCertificate.HasPrivateKey) {
      throw 'The exported CER does not match the signing certificate.'
    }
  }
  finally {
    $exportedCertificate.Dispose()
  }
  return $cerPath
}

function Build-SignedAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'build', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '-p:EnablePackaging=true', '-p:EnableTestSigning=true',
    '-p:UapAppxPackageBuildMode=SideloadOnly',
    "-p:PackageCertificateThumbprint=$signingThumbprint",
    "-p:AppxPackageDir=$signedOutput/",
    "-p:AppxPackageVersion=$MsixVersion", "-p:Version=$Version",
    '--nologo', '-v', 'minimal'
  )

  $signedPackages = @(Get-ChildItem $signedOutput -Recurse -Filter *.msix -File)
  if ($signedPackages.Count -ne 1) {
    throw "Expected one signed MSIX, found $($signedPackages.Count)."
  }
  $signedPath = Join-Path $dist "Crimson-$Version-win-x64.msix"
  Copy-Item $signedPackages[0].FullName $signedPath
  # Repacked unsigned; Confirm-SignedAsset applies the release signature afterwards.
  Remove-BloatFromMsix $signedPath
  return $signedPath
}

function Trust-ReleaseCertificate {
  param([Parameter(Mandatory)][string]$CerPath)

  $script:removeTrustedRootCertificate = -not (Test-Path "Cert:\CurrentUser\Root\$signingThumbprint")
  $trustedRootCertificate = Import-Certificate `
    -FilePath $CerPath `
    -CertStoreLocation 'Cert:\CurrentUser\Root'
  $script:trustedRootThumbprint = $trustedRootCertificate.Thumbprint
}

function Assert-PackageIdentity {
  param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$MakeAppx
  )

  if (Test-Path $Destination) { throw 'Package verification output already exists.' }
  Invoke-Checked $MakeAppx @('unpack', '/p', $PackagePath, '/d', $Destination, '/o')
  Assert-LicenseFile $Destination
  [xml]$packageManifest = Get-Content (Join-Path $Destination 'AppxManifest.xml') -Raw
  $identity = $packageManifest.Package.Identity
  if ($identity.Name -cne $packageName) {
    throw "Package identity '$($identity.Name)' does not match '$packageName'."
  }
  if ($identity.Publisher -cne $publisher) {
    throw "Package publisher '$($identity.Publisher)' does not match '$publisher'."
  }
  if ($identity.Version -cne $MsixVersion) {
    throw "Package version '$($identity.Version)' does not match '$MsixVersion'."
  }
  Assert-ReservedDisplayNames $packageManifest
}

function Confirm-SignedAsset {
  param(
    [Parameter(Mandatory)][string]$SignedPath,
    [Parameter(Mandatory)][string]$CerPath
  )

  $signTool = Find-WindowsSdkTool 'signtool.exe'
  Invoke-Checked $signTool @(
    'sign', '/fd', 'SHA256', '/sha1', $signingThumbprint,
    '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $SignedPath)
  if (-not $SkipCertificateTrust) { Trust-ReleaseCertificate $CerPath }
  Invoke-Checked $signTool @('verify', '/pa', '/v', $SignedPath)

  $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
  Assert-PackageIdentity $SignedPath (Join-Path $staging 'signed-package') $makeAppx
}

function Build-StoreAsset {
  Clear-ProjectOutput
  Invoke-Checked 'dotnet' @(
    'build', $project,
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64',
    '-p:EnablePackaging=true', '-p:EnableTestSigning=false',
    '-p:UapAppxPackageBuildMode=StoreUpload',
    "-p:AppxPackageDir=$storeOutput/",
    "-p:AppxPackageVersion=$MsixVersion", "-p:Version=$Version",
    '--nologo', '-v', 'minimal'
  )

  $storePackages = @(Get-ChildItem $storeOutput -Recurse -Filter *.msixupload -File)
  if ($storePackages.Count -ne 1) {
    throw "Expected one Store .msixupload, found $($storePackages.Count)."
  }
  $destination = if ($Unsigned) { $staging } else { $dist }
  $storePath = Join-Path $destination "Crimson-$Version-partner-center.msixupload"
  Copy-Item $storePackages[0].FullName $storePath
  Remove-BloatFromMsixUpload $storePath
  return $storePath
}

function Confirm-StoreAsset {
  param([Parameter(Mandatory)][string]$StorePath)

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $uploadContents = Join-Path $staging 'store-upload'
  New-Item $uploadContents -ItemType Directory | Out-Null
  [IO.Compression.ZipFile]::ExtractToDirectory($StorePath, $uploadContents)

  $innerPackages = @(Get-ChildItem $uploadContents -Recurse -Filter *.msix -File)
  if ($innerPackages.Count -ne 1) {
    throw "Expected one MSIX inside the Store upload, found $($innerPackages.Count)."
  }

  $storePackageContents = Join-Path $staging 'store-package'
  $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
  Assert-PackageIdentity $innerPackages[0].FullName $storePackageContents $makeAppx
  if (Test-Path (Join-Path $storePackageContents 'AppxSignature.p7x')) {
    throw 'Partner Center package unexpectedly contains a local signature.'
  }
  return $innerPackages[0].FullName
}

function Write-ReleaseChecksums {
  param([Parameter(Mandatory)][string[]]$FileNames)

  $payloads = foreach ($name in $FileNames) { Get-Item (Join-Path $dist $name) }
  $checksums = foreach ($asset in $payloads) {
    $hash = (Get-FileHash $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
  }
  $checksums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

  $releaseFiles = @(Get-ChildItem $dist -File)
  if ($releaseFiles.Count -ne $FileNames.Count + 1) {
    throw "Expected $($FileNames.Count + 1) release files, found $($releaseFiles.Count)."
  }
  Write-Host "Prepared and verified $($releaseFiles.Count) release files for $Version."
}

function Restore-ReleaseEnvironment {
  [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
  Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue

  if ($removeTrustedRootCertificate -and $trustedRootThumbprint) {
    & certutil.exe -user -delstore Root $trustedRootThumbprint | Out-Null
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "Failed to remove temporary trusted root certificate $trustedRootThumbprint."
    }
  }
  foreach ($thumbprint in $importedCertificateThumbprints) {
    Remove-Item "Cert:\CurrentUser\My\$thumbprint" -Force -ErrorAction SilentlyContinue
  }
}

try {
  Assert-ReleaseInputs
  Initialize-ReleaseDirectories
  Set-PackageVersion
  Invoke-ReleaseTests
  Publish-PortableAsset
  if ($Unsigned) {
    $storePath = Build-StoreAsset
    $unsignedPackage = Confirm-StoreAsset $storePath
    Copy-Item $unsignedPackage (Join-Path $dist "Crimson-$Version-win-x64.msix")
    Write-ReleaseChecksums @(
      "Crimson-$Version-win-x64.zip",
      "Crimson-$Version-win-x64.msix"
    )
  }
  else {
    $signingCertificate = Get-ReleaseSigningCertificate
    $cerPath = Export-PublicCertificate $signingCertificate
    $signedPath = Build-SignedAsset
    Confirm-SignedAsset $signedPath $cerPath
    $storePath = Build-StoreAsset
    $null = Confirm-StoreAsset $storePath
    Write-ReleaseChecksums @(
      "Crimson-$Version-win-x64.zip",
      "Crimson-$Version-win-x64.msix",
      "Crimson-$Version.cer",
      "Crimson-$Version-partner-center.msixupload"
    )
  }
}
finally {
  Restore-ReleaseEnvironment
}
