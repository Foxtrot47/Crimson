[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$Tag,

  [Parameter(Mandatory)]
  [string]$Commit,

  [string]$GitHubOutput
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
  throw "Release tag '$Tag' must use canonical vX.Y.Z syntax."
}

$components = @(
  [int64]$Matches[1],
  [int64]$Matches[2],
  [int64]$Matches[3]
)
if ($components | Where-Object { $_ -gt 65535 }) {
  throw 'MSIX version components must be between 0 and 65535.'
}

& git fetch --no-tags origin main
if ($LASTEXITCODE -ne 0) { throw 'Failed to fetch origin/main.' }

& git merge-base --is-ancestor $Commit origin/main
if ($LASTEXITCODE -ne 0) {
  throw "Tagged commit $Commit is not part of origin/main."
}

$version = $Tag.Substring(1)
$msixVersion = "$version.0"
Write-Host "Validated release $Tag as MSIX version $msixVersion."

if ($GitHubOutput) {
  "version=$version" | Out-File -FilePath $GitHubOutput -Encoding utf8 -Append
  "msix_version=$msixVersion" | Out-File -FilePath $GitHubOutput -Encoding utf8 -Append
}
