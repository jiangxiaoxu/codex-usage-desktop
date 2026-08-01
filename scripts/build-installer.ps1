#requires -PSEdition Core
#requires -Version 7.4

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.3',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw "This installer build requires PowerShell Core. Run it with pwsh, not powershell.exe."
}
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    throw "This installer build requires PowerShell 7.4 or newer. Current version: $($PSVersionTable.PSVersion)."
}

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspace 'src\CodexUsage.App\CodexUsage.App.csproj'
$installerScript = Join-Path $workspace 'installer\codex-usage-desktop.nsi'
$licenseFile = Join-Path $workspace 'LICENSE'
$appIconFile = Join-Path $workspace 'assets\codex-usage-desktop.ico'
$outputRoot = Join-Path $workspace 'release\winui-installer'
$publishDirectory = Join-Path $outputRoot 'publish'
$workDirectory = Join-Path $outputRoot 'work'
$uninstallInclude = Join-Path $workDirectory 'uninstall-publish-files.nsh'
$setupPath = Join-Path $outputRoot "codex-usage-desktop-setup-$Version-x64.exe"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,
        [Parameter(Mandatory)]
        [string]$Parent
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $candidateFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside '$parentFull': $candidateFull"
    }
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-ChildPath -Candidate $Path -Parent $outputRoot
    if ([System.IO.Directory]::Exists($Path)) {
        [System.IO.Directory]::Delete($Path, $true)
    }
    [System.IO.Directory]::CreateDirectory($Path) | Out-Null
}

function Publish-InstallerSetup {
    param(
        [Parameter(Mandatory)][string]$PendingPath,
        [Parameter(Mandatory)][string]$SetupPath
    )

    $pendingFull = [System.IO.Path]::GetFullPath($PendingPath)
    $setupFull = [System.IO.Path]::GetFullPath($SetupPath)
    if (-not [string]::Equals(
        [System.IO.Path]::GetPathRoot($pendingFull),
        [System.IO.Path]::GetPathRoot($setupFull),
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer publish requires pending and final paths on the same volume: $pendingFull -> $setupFull"
    }
    if (-not [System.IO.File]::Exists($pendingFull)) {
        throw "Pending installer is missing: $pendingFull"
    }
    $pending = Get-Item -LiteralPath $pendingFull
    if ($pending.Length -le 0) {
        throw "Pending installer is empty: $pendingFull"
    }

    if ([System.IO.File]::Exists($setupFull)) {
        [System.IO.File]::Replace(
            $pendingFull,
            $setupFull,
            [System.Management.Automation.Language.NullString]::Value,
            $true)
    }
    else {
        [System.IO.File]::Move($pendingFull, $setupFull)
    }

    $setup = Get-Item -LiteralPath $setupFull
    if ($setup.Length -le 0) {
        throw "Published installer is empty: $setupFull"
    }
    return [pscustomobject]@{
        Path = $setup.FullName
        Size = $setup.Length
        SHA256 = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash
    }
}

function Find-MakeNsis {
    $command = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    foreach ($candidate in @(
        'C:\Program Files (x86)\NSIS\makensis.exe',
        'C:\Program Files\NSIS\makensis.exe'
    )) {
        if ([System.IO.File]::Exists($candidate)) {
            return $candidate
        }
    }

    $electronBuilderCache = Join-Path $env:LOCALAPPDATA 'electron-builder\Cache'
    if ([System.IO.Directory]::Exists($electronBuilderCache)) {
        $cached = Get-ChildItem -LiteralPath $electronBuilderCache -Filter 'makensis.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1
        if ($null -ne $cached) {
            return $cached.FullName
        }
    }

    throw 'makensis.exe was not found. Install NSIS 3.x or add makensis.exe to PATH.'
}

function Convert-ToNsisPath {
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-AppIcon {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6 -or
        [System.BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [System.BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw "Application icon is not a valid ICO file: $Path"
    }

    $imageCount = [System.BitConverter]::ToUInt16($bytes, 4)
    $directoryLength = 6 + (16 * $imageCount)
    if ($imageCount -eq 0 -or $directoryLength -gt $bytes.Length) {
        throw "Application icon has an invalid image directory: $Path"
    }

    $sizes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $imageCount; $index++) {
        $entryOffset = 6 + (16 * $index)
        $width = if ($bytes[$entryOffset] -eq 0) { 256 } else { [int]$bytes[$entryOffset] }
        $height = if ($bytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$bytes[$entryOffset + 1] }
        $imageLength = [System.BitConverter]::ToUInt32($bytes, $entryOffset + 8)
        $imageOffset = [System.BitConverter]::ToUInt32($bytes, $entryOffset + 12)
        if ($imageLength -eq 0 -or ([uint64]$imageOffset + $imageLength) -gt $bytes.Length) {
            throw "Application icon contains an invalid image entry: $Path"
        }
        $null = $sizes.Add("${width}x${height}")
    }

    foreach ($requiredSize in @('16x16', '32x32', '48x48', '256x256')) {
        if (-not $sizes.Contains($requiredSize)) {
            throw "Application icon is missing required size ${requiredSize}: $Path"
        }
    }
}

function Get-RelativeChildPath {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [System.IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not a child of '$parentFull': $childFull"
    }
    return $childFull.Substring($parentFull.Length)
}

function Write-UninstallManifest {
    param(
        [Parameter(Mandatory)][string]$PublishPath,
        [Parameter(Mandatory)][string]$Destination
    )

    $publishFull = [System.IO.Path]::GetFullPath($PublishPath).TrimEnd('\')
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('; Generated by scripts/build-installer.ps1. Do not edit.')

    $files = Get-ChildItem -LiteralPath $publishFull -File -Recurse | Sort-Object FullName -Descending
    foreach ($file in $files) {
        $relative = (Get-RelativeChildPath -Parent $publishFull -Child $file.FullName).Replace('/', '\')
        if ($relative.Contains('$') -or $relative.Contains('"')) {
            throw "Publish path cannot be represented safely in NSIS: $relative"
        }
        $lines.Add("Delete `"`$INSTDIR\$relative`"")
    }

    $directories = Get-ChildItem -LiteralPath $publishFull -Directory -Recurse |
        Sort-Object @{ Expression = { $_.FullName.Length }; Descending = $true }, FullName
    foreach ($directory in $directories) {
        $relative = (Get-RelativeChildPath -Parent $publishFull -Child $directory.FullName).Replace('/', '\')
        if ($relative.Contains('$') -or $relative.Contains('"')) {
            throw "Publish path cannot be represented safely in NSIS: $relative"
        }
        $lines.Add("RMDir `"`$INSTDIR\$relative`"")
    }

    [System.IO.File]::WriteAllLines($Destination, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Assert-InstallerSafety {
    param([Parameter(Mandatory)][string]$Path)

    $source = [System.IO.File]::ReadAllText($Path)
    foreach ($required in @(
        'Call EnsureAppClosed',
        'Call BackupLedger',
        'Call UninstallLegacyElectron',
        'Call RemoveInstalledPayload',
        'Call DeployPayload',
        'taskkill.exe',
        '/S /allusers',
        'Call RestoreLedgerAfterLegacyUninstall',
        '!include "${UNINSTALL_FILES_INCLUDE}"',
        'File /r "${PUBLISH_DIR}\*.*"',
        '!ifndef APP_ICON_FILE',
        '!define MUI_ICON "${APP_ICON_FILE}"',
        '!define MUI_UNICON "${APP_ICON_FILE}"',
        'ReadRegStr $ExistingStartupRun HKCU',
        'StrCmp $CurrentAdminOptIn "1" current_admin_confirmed'
    )) {
        if ($source.IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
            throw "Installer safety invariant is missing: $required"
        }
    }
    if ($source.IndexOf('RMDir /r "$INSTDIR"', [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Installer contains a recursive deletion of INSTDIR.'
    }
    $coreStart = $source.IndexOf('Section "$(SectionProgram)"', [System.StringComparison]::Ordinal)
    $coreEnd = $source.IndexOf('SectionEnd', $coreStart, [System.StringComparison]::Ordinal)
    $core = $source.Substring($coreStart, $coreEnd - $coreStart)
    $userLedgerContextPattern = [System.Text.RegularExpressions.Regex]::new(
        'Call EnsureAppClosed\s+SetShellVarContext current\s+Call BackupLedger\s+Call UninstallLegacyElectron\s+SetShellVarContext all\s+Call RemoveInstalledPayload',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $userLedgerContextPattern.IsMatch($core)) {
        throw 'Installer must use the current-user shell context for ledger backup and legacy restore, then restore the all-users context before replacing program files.'
    }
    $orderedCalls = @(
        'Call EnsureAppClosed',
        'Call BackupLedger',
        'Call UninstallLegacyElectron',
        'Call RemoveInstalledPayload',
        'Call DeployPayload'
    )
    $previousIndex = -1
    foreach ($call in $orderedCalls) {
        $index = $core.IndexOf($call, [System.StringComparison]::Ordinal)
        if ($index -le $previousIndex) {
            throw "Installer replacement order is invalid at: $call"
        }
        $previousIndex = $index
    }
    $legacyStart = $source.IndexOf('Function UninstallLegacyElectron', [System.StringComparison]::Ordinal)
    $legacyEnd = $source.IndexOf('FunctionEnd', $legacyStart, [System.StringComparison]::Ordinal)
    $legacyUninstall = $source.Substring($legacyStart, $legacyEnd - $legacyStart)
    $legacyUninstallSteps = @(
        'InitPluginsDir',
        'CopyFiles /SILENT "$INSTDIR\${UNINSTALL_EXE}" "$PLUGINSDIR\${UNINSTALL_EXE}"',
        'IfErrors legacy_uninstall_failed',
        'nsExec::ExecToStack ''"$PLUGINSDIR\${UNINSTALL_EXE}" /S /allusers _?=$INSTDIR'''
    )
    $previousIndex = -1
    foreach ($step in $legacyUninstallSteps) {
        $index = $legacyUninstall.IndexOf($step, [System.StringComparison]::Ordinal)
        if ($index -le $previousIndex) {
            throw "Legacy Electron uninstall synchronization is invalid at: $step"
        }
        $previousIndex = $index
    }
    if ($legacyUninstall.IndexOf(
        'nsExec::ExecToStack ''"$INSTDIR\${UNINSTALL_EXE}"',
        [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Legacy Electron uninstaller must run from PLUGINSDIR.'
    }
    foreach ($forbidden in @(
        'StagingDir',
        'PreviousDir',
        '__codex_disabled_',
        'RollbackActivatedPayload',
        'RestorePrevious',
        'Rename "'
    )) {
        if ($source.IndexOf($forbidden, [System.StringComparison]::Ordinal) -ge 0) {
            throw "Removed transactional upgrade mechanism remains: $forbidden"
        }
    }
    $silentStart = $source.IndexOf('silent_admin_check:', [System.StringComparison]::Ordinal)
    $silentEnd = $source.IndexOf('current_admin_confirmed:', $silentStart, [System.StringComparison]::Ordinal)
    $silentGate = $source.Substring($silentStart, $silentEnd - $silentStart)
    if (-not ($silentGate.IndexOf('StrCmp $CurrentAdminOptIn "1" current_admin_confirmed', [System.StringComparison]::Ordinal) -ge 0 -and
        $silentGate.IndexOf('SetErrorLevel 5', [System.StringComparison]::Ordinal) -ge 0 -and
        $silentGate.IndexOf('Abort', [System.StringComparison]::Ordinal) -ge 0)) {
        throw 'Silent setup must abort unless /CURRENTADMIN=1 is present.'
    }
    $unsafeMessageBoxes = [System.Text.RegularExpressions.Regex]::Matches(
        $source,
        '(?m)^\s*MessageBox\s+(?!.*\s/SD\s).*$')
    if ($unsafeMessageBoxes.Count -ne 0) {
        throw "Installer contains $($unsafeMessageBoxes.Count) MessageBox command(s) without /SD."
    }
}

function Assert-WinUiPublish {
    param(
        [Parameter(Mandatory)][string]$PublishPath,
        [Parameter(Mandatory)][string]$IconPath
    )

    foreach ($relativePath in @(
        'Codex Usage Desktop.pri',
        'App.xbf',
        'MainWindow.xbf',
        'Controls\AuditFilterContent.xbf',
        'Controls\CostRow.xbf'
    )) {
        $path = Join-Path $PublishPath $relativePath
        if (-not [System.IO.File]::Exists($path)) {
            throw "Unpackaged WinUI runtime resource is missing: $path"
        }
    }

    $publishedIcon = Join-Path $PublishPath 'Assets\codex-usage-desktop.ico'
    if (-not [System.IO.File]::Exists($publishedIcon)) {
        throw "Published application icon is missing: $publishedIcon"
    }
    $sourceHash = (Get-FileHash -LiteralPath $IconPath -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath $publishedIcon -Algorithm SHA256).Hash
    if ($publishedHash -ne $sourceHash) {
        throw "Published application icon does not match the tracked source asset: $publishedIcon"
    }
}

function Invoke-PublishedSmokeTest {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    Write-Host "Running bounded WinUI smoke test: $ExecutablePath --smoke-test"
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList '--smoke-test' -PassThru
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'Published WinUI smoke test exceeded the 30 second budget.'
    }

    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        throw "Published WinUI smoke test failed with exit code $($process.ExitCode)."
    }
}

if (-not [System.IO.File]::Exists($projectPath)) {
    throw "WinUI project not found: $projectPath"
}
if (-not [System.IO.File]::Exists($installerScript)) {
    throw "NSIS script not found: $installerScript"
}
if (-not [System.IO.File]::Exists($appIconFile)) {
    throw "Application icon not found: $appIconFile"
}
Assert-AppIcon -Path $appIconFile
Assert-InstallerSafety -Path $installerScript
if ($ValidateOnly) {
    Write-Host "Installer static validation passed: $installerScript"
    return
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
Reset-GeneratedDirectory -Path $publishDirectory
Reset-GeneratedDirectory -Path $workDirectory

$pendingSetupPath = Join-Path $workDirectory "codex-usage-desktop-setup-$Version-x64-$([System.Guid]::NewGuid().ToString('N')).pending.exe"
try {
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--output', $publishDirectory,
        '-p:Platform=x64',
        '-p:WindowsPackageType=None',
        '-p:EnableMsixTooling=false',
        '-p:DisableMsixProjectCapabilityAddedByProject=true',
        '-p:GenerateAppxPackageOnBuild=false',
        '-p:AppxPackageSigningEnabled=false',
        '-p:SelfContained=true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version"
    )

    Write-Host "Publishing unpackaged WinUI app to $publishDirectory"
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $applicationExe = Join-Path $publishDirectory 'Codex Usage Desktop.exe'
    if (-not [System.IO.File]::Exists($applicationExe)) {
        throw "Publish did not produce the expected executable: $applicationExe"
    }

    Assert-WinUiPublish -PublishPath $publishDirectory -IconPath $appIconFile
    Invoke-PublishedSmokeTest -ExecutablePath $applicationExe
    Write-UninstallManifest -PublishPath $publishDirectory -Destination $uninstallInclude
    $makeNsis = Find-MakeNsis
    $fileVersion = "$Version.0"
    $makeNsisArguments = @(
        '/V3',
        '/WX',
        '/INPUTCHARSET', 'UTF8',
        "/DPRODUCT_VERSION=$Version",
        "/DPRODUCT_FILE_VERSION=$fileVersion",
        "/DPUBLISH_DIR=$(Convert-ToNsisPath $publishDirectory)",
        "/DOUTPUT_FILE=$(Convert-ToNsisPath $pendingSetupPath)",
        "/DUNINSTALL_FILES_INCLUDE=$(Convert-ToNsisPath $uninstallInclude)",
        "/DLICENSE_FILE=$(Convert-ToNsisPath $licenseFile)",
        "/DAPP_ICON_FILE=$(Convert-ToNsisPath $appIconFile)",
        (Convert-ToNsisPath $installerScript)
    )

    Write-Host "Compiling NSIS setup with $makeNsis"
    & $makeNsis @makeNsisArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $published = Publish-InstallerSetup -PendingPath $pendingSetupPath -SetupPath $setupPath
    Write-Host "Setup: $($published.Path)"
    Write-Host "Size: $($published.Size) bytes"
    Write-Host "SHA256: $($published.SHA256)"

    [pscustomobject]@{
        Setup = $published.Path
        Size = $published.Size
        SHA256 = $published.SHA256
        MakeNsis = $makeNsis
    }
}
finally {
    if ([System.IO.File]::Exists($pendingSetupPath)) {
        [System.IO.File]::Delete($pendingSetupPath)
    }
}
