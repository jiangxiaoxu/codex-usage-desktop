[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param()

$ErrorActionPreference = "Stop"

$workspace = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$productName = "Codex Usage Desktop"
$sourceDirectory = [System.IO.Path]::GetFullPath((Join-Path $workspace "release\codex-usage-data"))
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) {
  throw "Windows LocalAppData is unavailable."
}

$destinationDirectory = [System.IO.Path]::GetFullPath((Join-Path $localAppData $productName))
$backupParent = [System.IO.Path]::GetFullPath((Join-Path $localAppData "$productName Migration Backups"))
$timestamp = [DateTimeOffset]::Now.ToString("yyyyMMdd-HHmmss")
$backupDirectory = Join-Path $backupParent "legacy-ledger-$timestamp"
$backupStagingDirectory = "$backupDirectory.partial-$PID"
$migrationStagingDirectory = Join-Path $localAppData "$productName.migration-$PID"

function Get-Sha256([string]$FilePath) {
  $stream = [System.IO.File]::OpenRead($FilePath)
  $hasher = [System.Security.Cryptography.SHA256]::Create()
  try {
    return ([System.BitConverter]::ToString($hasher.ComputeHash($stream))).Replace("-", "")
  } finally {
    $hasher.Dispose()
    $stream.Dispose()
  }
}

function Resolve-ThroughExistingAncestor([string]$Candidate) {
  $fullPath = [System.IO.Path]::GetFullPath($Candidate)
  $root = [System.IO.Path]::GetPathRoot($fullPath)
  if ([string]::IsNullOrEmpty($root)) {
    throw "Path has no filesystem root: $Candidate"
  }
  $relative = $fullPath.Substring($root.Length)
  $separators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
  $segments = $relative.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)
  $current = $root

  for ($index = 0; $index -lt $segments.Length; $index++) {
    $next = Join-Path $current $segments[$index]
    if (-not (Test-Path -LiteralPath $next)) {
      for ($remainder = $index; $remainder -lt $segments.Length; $remainder++) {
        $current = Join-Path $current $segments[$remainder]
      }
      return [System.IO.Path]::GetFullPath($current)
    }

    $item = Get-Item -LiteralPath $next -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
      $target = $item.ResolveLinkTarget($true)
      if ($null -eq $target) {
        throw "Unable to resolve reparse point: $next"
      }
      $current = $target.FullName
    } else {
      $current = $item.FullName
    }
  }
  return [System.IO.Path]::GetFullPath($current)
}

function Assert-OutsideProtectedCodexTree([string]$Candidate) {
  $codexHome = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".codex"
  $resolvedCandidate = (Resolve-ThroughExistingAncestor $Candidate).TrimEnd("\")
  foreach ($relativePath in @("sessions", "archived_sessions", "agents")) {
    $protected = (Resolve-ThroughExistingAncestor (Join-Path $codexHome $relativePath)).TrimEnd("\")
    if ($resolvedCandidate.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedCandidate.StartsWith("$protected\", [StringComparison]::OrdinalIgnoreCase)) {
      throw "Migration output must remain outside protected Codex source directories: $Candidate"
    }
  }
}

function Copy-VerifiedDirectory([string]$Source, [string]$Destination) {
  [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
  foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
  }

  $sourceFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -Force -File)
  foreach ($sourceFile in $sourceFiles) {
    $relativePath = $sourceFile.FullName.Substring($Source.Length).TrimStart("\")
    $copiedFile = Join-Path $Destination $relativePath
    if (-not (Test-Path -LiteralPath $copiedFile -PathType Leaf)) {
      throw "Verified copy is incomplete: $relativePath"
    }
    $copiedItem = Get-Item -LiteralPath $copiedFile
    if ($sourceFile.Length -ne $copiedItem.Length -or
        (Get-Sha256 $sourceFile.FullName) -ne (Get-Sha256 $copiedFile)) {
      throw "Verified copy hash mismatch: $relativePath"
    }
  }
}

if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
  Write-Host "No legacy release ledger directory exists: $sourceDirectory"
  return
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory "usage.sqlite") -PathType Leaf)) {
  throw "Legacy ledger database is missing: $sourceDirectory"
}
if (Test-Path -LiteralPath $destinationDirectory) {
  throw "Destination ledger directory already exists. Refusing to overwrite: $destinationDirectory"
}
foreach ($path in @($destinationDirectory, $backupDirectory, $backupStagingDirectory, $migrationStagingDirectory)) {
  Assert-OutsideProtectedCodexTree $path
  if (Test-Path -LiteralPath $path) {
    throw "Migration work path already exists. Refusing to overwrite: $path"
  }
}

$runningInstances = @(Get-Process | Where-Object {
  $_.ProcessName -eq "CodexUsage.App" -or $_.ProcessName -like "$productName*"
})
if ($runningInstances.Count -gt 0) {
  throw "Close Codex Usage Desktop before migrating its SQLite ledger."
}

Write-Host "Legacy ledger migration plan:"
Write-Host "  Source:      $sourceDirectory"
Write-Host "  Backup:      $backupDirectory"
Write-Host "  Destination: $destinationDirectory"
Write-Host "  Source deletion: disabled"

$action = "Create a verified backup and copy the legacy ledger without deleting its source"
if (-not $PSCmdlet.ShouldProcess($destinationDirectory, $action)) {
  return
}

$backupComplete = $false
try {
  [System.IO.Directory]::CreateDirectory($backupParent) | Out-Null
  Copy-VerifiedDirectory $sourceDirectory $backupStagingDirectory
  [System.IO.Directory]::Move($backupStagingDirectory, $backupDirectory)
  $backupComplete = $true

  Copy-VerifiedDirectory $sourceDirectory $migrationStagingDirectory
  [System.IO.Directory]::Move($migrationStagingDirectory, $destinationDirectory)

  Write-Host "Verified backup created: $backupDirectory"
  Write-Host "Ledger copied to: $destinationDirectory"
  Write-Host "Legacy source retained: $sourceDirectory"
} catch {
  foreach ($partialDirectory in @($backupStagingDirectory, $migrationStagingDirectory)) {
    if ([System.IO.Directory]::Exists($partialDirectory)) {
      [System.IO.Directory]::Delete($partialDirectory, $true)
    }
  }
  if (-not $backupComplete -and [System.IO.Directory]::Exists($backupDirectory)) {
    [System.IO.Directory]::Delete($backupDirectory, $true)
  }
  throw
}
