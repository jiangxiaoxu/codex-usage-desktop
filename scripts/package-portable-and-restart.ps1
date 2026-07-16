param(
  [switch]$Rebuild
)

$ErrorActionPreference = "Stop"

$workspace = Split-Path -Parent $PSScriptRoot
$packagePath = Join-Path $workspace "package.json"
$package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
$productName = [string]$package.build.productName
$version = [string]$package.version

if ([string]::IsNullOrWhiteSpace($productName) -or [string]::IsNullOrWhiteSpace($version)) {
  throw "package.json must define build.productName and version."
}

$executableName = "$productName.exe"
$releaseDirectory = Join-Path $workspace "release"
$portableExecutable = Join-Path $releaseDirectory "$productName $version.exe"
$portableLauncherName = Split-Path $portableExecutable -Leaf
$portableLauncherPattern = "^$([regex]::Escape($productName)) [0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?\.exe$"
$runtimeDirectory = Join-Path $workspace "work\portable-run"
$runtimeExecutable = Join-Path $runtimeDirectory $portableLauncherName
$controlDirectory = Join-Path $workspace "work\portable-control"
$controlExecutable = Join-Path $controlDirectory $portableLauncherName
$unpackedAsar = Join-Path $releaseDirectory "win-unpacked\resources\app.asar"
$developmentDirectory = Join-Path $workspace "node_modules\electron\dist"
$developmentExecutable = Join-Path $developmentDirectory "electron.exe"
$ledgerDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) $productName

function Get-ApplicationInstances {
  return @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match $portableLauncherPattern -or
      (($_.Name -eq $executableName) -and ($_.CommandLine -notmatch "\s--type=")) -or
      (($_.Name -eq "electron.exe") -and ($_.ExecutablePath -eq $developmentExecutable) -and ($_.CommandLine -notmatch "\s--type="))
  })
}

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

function Stop-ProcessTree([int[]]$rootProcessIds) {
  $processes = @(Get-CimInstance Win32_Process)
  $processIds = [System.Collections.Generic.HashSet[int]]::new()
  foreach ($rootProcessId in $rootProcessIds) {
    [void]$processIds.Add($rootProcessId)
  }
  do {
    $addedChild = $false
    foreach ($process in $processes) {
      if ($processIds.Contains([int]$process.ParentProcessId) -and $processIds.Add([int]$process.ProcessId)) {
        $addedChild = $true
      }
    }
  } while ($addedChild)
  foreach ($processId in @($processIds) | Sort-Object -Descending) {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
  }
}

function Stop-ManagedApplicationInstances {
  $rootProcessIds = @(Get-CimInstance Win32_Process |
    Where-Object {
      $_.ExecutablePath -like "$runtimeDirectory\*" -or
        $_.ExecutablePath -like "$controlDirectory\*" -or
        $_.ExecutablePath -like "$releaseDirectory\$productName *.exe" -or
        $_.ExecutablePath -eq $developmentExecutable
    } |
    ForEach-Object { [int]$_.ProcessId })
  if ($rootProcessIds.Count -gt 0) {
    Stop-ProcessTree $rootProcessIds
  }
}

function Request-ApplicationShutdown {
  $existingInstances = @(Get-ApplicationInstances)
  if ($existingInstances.Count -eq 0) {
    return
  }

  if (-not (Test-Path -LiteralPath $portableExecutable -PathType Leaf)) {
    throw "A $productName instance is running, but the portable executable is unavailable."
  }

  Write-Host "Requesting shutdown from the current workspace application instance..."
  New-Item -ItemType Directory -Path $controlDirectory -Force | Out-Null
  Copy-Item -LiteralPath $portableExecutable -Destination $controlExecutable -Force
  $unpackedDirectory = Join-Path $releaseDirectory "win-unpacked"
  Start-Process -FilePath $controlExecutable -ArgumentList @(
    "--shutdown-for-restart=`"$releaseDirectory`"",
    "--shutdown-for-restart=`"$runtimeDirectory`"",
    "--shutdown-for-restart=`"$unpackedDirectory`"",
    "--shutdown-for-restart=`"$developmentDirectory`"",
    "--shutdown-for-data-directory=`"$(Join-Path $releaseDirectory 'codex-usage-data')`"",
    "--shutdown-for-data-directory=`"$ledgerDirectory`""
  ) -WorkingDirectory $controlDirectory
  $shutdownDeadline = (Get-Date).AddSeconds(5)
  do {
    Start-Sleep -Milliseconds 250
    $remainingInstances = @(Get-ApplicationInstances)
  } while ($remainingInstances.Count -gt 0 -and (Get-Date) -lt $shutdownDeadline)

  if ($remainingInstances.Count -gt 0) {
    Write-Host "Graceful shutdown timed out; closing only managed workspace processes..."
    Stop-ManagedApplicationInstances
    $forceShutdownDeadline = (Get-Date).AddSeconds(5)
    do {
      Start-Sleep -Milliseconds 250
      $remainingInstances = @(Get-ApplicationInstances)
    } while ($remainingInstances.Count -gt 0 -and (Get-Date) -lt $forceShutdownDeadline)
  }

  if ($remainingInstances.Count -gt 0) {
    $paths = ($remainingInstances | ForEach-Object { $_.ExecutablePath } | Where-Object { $_ } | Sort-Object -Unique) -join "; "
    throw "A $productName instance did not accept this workspace restart request ($paths). Close it before running this command."
  }
}

Request-ApplicationShutdown

if ($Rebuild) {
  $buildStartedAt = [DateTime]::UtcNow
  Push-Location $workspace
  try {
    & npm.cmd run package:portable
    if ($LASTEXITCODE -ne 0) {
      throw "Portable packaging failed with exit code $LASTEXITCODE."
    }
  } finally {
    Pop-Location
  }
}

if (-not (Test-Path -LiteralPath $portableExecutable -PathType Leaf)) {
  throw "Portable executable was not created: $portableExecutable"
}

$artifact = Get-Item -LiteralPath $portableExecutable
if ($Rebuild -and $artifact.LastWriteTimeUtc -lt $buildStartedAt) {
  throw "Portable executable was not refreshed by this build: $portableExecutable"
}
if (-not (Test-Path -LiteralPath $unpackedAsar -PathType Leaf)) {
  throw "Packaged app.asar was not created: $unpackedAsar"
}

$expectedAsarHash = Get-Sha256 $unpackedAsar

Request-ApplicationShutdown
Write-Host "Launching refreshed portable executable copy: $runtimeExecutable"
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
Copy-Item -LiteralPath $portableExecutable -Destination $runtimeExecutable -Force

$launchProcess = Start-Process -FilePath $runtimeExecutable -WorkingDirectory $runtimeDirectory -PassThru

try {
  $launchDeadline = (Get-Date).AddSeconds(15)
  do {
    Start-Sleep -Milliseconds 250
    $launchedInstances = @(Get-ApplicationInstances)
    $processesById = @{}
    Get-CimInstance Win32_Process | ForEach-Object { $processesById[[int]$_.ProcessId] = $_ }
    $matchingAsar = $launchedInstances | ForEach-Object {
      $currentProcess = $_
      $isRuntimeDescendant = $false
      while ($currentProcess -ne $null) {
        if ([int]$currentProcess.ParentProcessId -eq $launchProcess.Id) {
          $isRuntimeDescendant = $true
          break
        }
        $currentProcess = $processesById[[int]$currentProcess.ParentProcessId]
      }
      if ($isRuntimeDescendant -and -not [string]::IsNullOrWhiteSpace($_.ExecutablePath)) {
        $asar = Join-Path (Split-Path -Parent $_.ExecutablePath) "resources\app.asar"
        if ((Test-Path -LiteralPath $asar -PathType Leaf) -and ((Get-Sha256 $asar) -eq $expectedAsarHash)) {
          $asar
        }
      }
    } | Select-Object -First 1
  } while ($null -eq $matchingAsar -and (Get-Date) -lt $launchDeadline)

  if ($null -eq $matchingAsar) {
    throw "The launched application does not match this build's app.asar."
  }
} catch {
  Stop-ProcessTree @($launchProcess.Id)
  throw
}
