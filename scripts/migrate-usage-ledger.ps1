$ErrorActionPreference = "Stop"

$workspace = Split-Path -Parent $PSScriptRoot
$package = Get-Content -LiteralPath (Join-Path $workspace "package.json") -Raw | ConvertFrom-Json
$productName = [string]$package.build.productName
$version = [string]$package.version
$sourceDirectory = Join-Path $workspace "release\codex-usage-data"
$destinationDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) $productName

function Get-Sha256([string]$filePath) {
  $stream = [System.IO.File]::OpenRead($filePath)
  $hasher = [System.Security.Cryptography.SHA256]::Create()
  try {
    return ([System.BitConverter]::ToString($hasher.ComputeHash($stream))).Replace("-", "")
  } finally {
    $hasher.Dispose()
    $stream.Dispose()
  }
}

if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
  Write-Host "No legacy portable ledger directory exists: $sourceDirectory"
  exit 0
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory "usage.sqlite") -PathType Leaf)) {
  throw "Legacy ledger database is missing: $sourceDirectory"
}
if (Test-Path -LiteralPath $destinationDirectory) {
  throw "Destination ledger directory already exists. Refusing to overwrite: $destinationDirectory"
}

$portableLauncherPattern = "^$([regex]::Escape($productName)) [0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?\.exe$"
$developmentExecutable = Join-Path $workspace "node_modules\electron\dist\electron.exe"
$runningInstances = @(Get-CimInstance Win32_Process | Where-Object {
  $_.Name -eq "$productName.exe" -or $_.Name -match $portableLauncherPattern -or $_.ExecutablePath -eq $developmentExecutable
})
if ($runningInstances.Count -gt 0) {
  throw "Close Codex Usage Desktop before migrating its SQLite ledger."
}

$destinationParent = Split-Path -Parent $destinationDirectory
$stagingDirectory = Join-Path $destinationParent "$productName.migration-$PID"
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
  Get-ChildItem -LiteralPath $sourceDirectory -Force | Copy-Item -Destination $stagingDirectory -Recurse -Force
  $sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Force -File)
  foreach ($sourceFile in $sourceFiles) {
    $relativePath = $sourceFile.FullName.Substring($sourceDirectory.Length).TrimStart("\\")
    $stagedFile = Join-Path $stagingDirectory $relativePath
    if (-not (Test-Path -LiteralPath $stagedFile -PathType Leaf)) {
      throw "Migration copy is incomplete: $relativePath"
    }
    if ($sourceFile.Length -ne (Get-Item -LiteralPath $stagedFile).Length -or (Get-Sha256 $sourceFile.FullName) -ne (Get-Sha256 $stagedFile)) {
      throw "Migration copy verification failed: $relativePath"
    }
  }
  Move-Item -LiteralPath $stagingDirectory -Destination $destinationDirectory
  Remove-Item -LiteralPath $sourceDirectory -Recurse -Force
  Write-Host "Migrated ledger to: $destinationDirectory"
} catch {
  if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
  }
  throw
}
