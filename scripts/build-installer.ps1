#requires -PSEdition Core
#requires -Version 7.4

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.21',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$SevenZipPath,

    [string]$SevenZipRuntimePath,

    [switch]$AutoDetectDependencies,

    [string[]]$DependencySearchDirectory = @(),

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
$payloadArchive = Join-Path $workDirectory 'payload.7z'
$payloadValidationDirectory = Join-Path $workDirectory 'payload-validation'
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
    param([string]$VerifiedPath)

    if (-not [string]::IsNullOrWhiteSpace($VerifiedPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($VerifiedPath)
        if (-not [string]::Equals(
            [System.IO.Path]::GetFileName($fullPath),
            'makensis.exe',
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Auto-detected NSIS path must be makensis.exe: $fullPath"
        }
        if (-not [System.IO.File]::Exists($fullPath)) {
            throw "Auto-detected NSIS executable was not found: $fullPath"
        }

        try {
            $versionOutput = @(& $fullPath '/VERSION' 2>&1)
        }
        catch {
            throw "Auto-detected NSIS executable could not be run: $fullPath"
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Auto-detected NSIS executable returned exit code ${LASTEXITCODE}: $fullPath"
        }

        $versionText = ($versionOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $versionText,
            '(?<!\d)3\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.]+)?(?!\d)')) {
            throw "Auto-detected NSIS executable is not NSIS 3.x: $fullPath"
        }

        return $fullPath
    }

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

    throw 'makensis.exe was not found. Install NSIS 3.x or add makensis.exe to PATH.'
}

function Resolve-RequiredExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ParameterName,
        [Parameter(Mandatory)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description is required. Pass -$ParameterName with an explicit local executable path. The installer build does not download or install 7-Zip."
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($fullPath)) {
        throw "$Description was not found: $fullPath. Pass -$ParameterName with an existing local executable path."
    }
    if (-not $fullPath.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be an .exe file: $fullPath"
    }

    return $fullPath
}

function ConvertTo-DependencySearchDirectories {
    param(
        [AllowEmptyCollection()]
        [string[]]$Directories = @()
    )

    $normalizedDirectories = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Directories) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        foreach ($directory in $entry.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmedDirectory = $directory.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmedDirectory)) {
                continue
            }

            try {
                $fullDirectory = [System.IO.Path]::TrimEndingDirectorySeparator(
                    [System.IO.Path]::GetFullPath($trimmedDirectory))
            }
            catch {
                continue
            }

            if ($seen.Add($fullDirectory)) {
                $normalizedDirectories.Add($fullDirectory)
            }
        }
    }

    return $normalizedDirectories.ToArray()
}

function Resolve-AutoDetectedPackagingDependencies {
    param(
        [AllowEmptyString()]
        [string]$BuilderPath,

        [AllowEmptyString()]
        [string]$RuntimePath,

        [AllowEmptyCollection()]
        [string[]]$SearchDirectories = @()
    )

    $dependencyLocator = Join-Path $PSScriptRoot 'find-release-packaging-dependencies.ps1'
    if (-not [System.IO.File]::Exists($dependencyLocator)) {
        throw "Release packaging dependency locator not found: $dependencyLocator"
    }

    $locatorArguments = @{
        RequireAll = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($BuilderPath)) {
        $locatorArguments['SevenZipPath'] = $BuilderPath
    }
    if (-not [string]::IsNullOrWhiteSpace($RuntimePath)) {
        $locatorArguments['SevenZipRuntimePath'] = $RuntimePath
    }
    $normalizedSearchDirectories = [string[]]@(
        ConvertTo-DependencySearchDirectories -Directories $SearchDirectories
    )
    if ($normalizedSearchDirectories.Count -gt 0) {
        $locatorArguments['SearchDirectory'] = $normalizedSearchDirectories
    }

    $dependencies = & $dependencyLocator @locatorArguments
    if ($null -eq $dependencies -or -not $dependencies.Ready -or
        [string]::IsNullOrWhiteSpace($dependencies.DotnetPath) -or
        [string]::IsNullOrWhiteSpace($dependencies.MakeNsisPath) -or
        [string]::IsNullOrWhiteSpace($dependencies.SevenZipPath) -or
        [string]::IsNullOrWhiteSpace($dependencies.SevenZipRuntimePath)) {
        throw 'Release packaging dependency locator did not return ready packaging dependency paths.'
    }

    return [pscustomobject]@{
        DotnetPath = [string]$dependencies.DotnetPath
        MakeNsisPath = [string]$dependencies.MakeNsisPath
        SevenZipPath = [string]$dependencies.SevenZipPath
        SevenZipRuntimePath = [string]$dependencies.SevenZipRuntimePath
    }
}

function Invoke-CheckedExecutable {
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Operation,
        [string]$WorkingDirectory
    )

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $null = & $ExecutablePath @Arguments
    }
    else {
        Push-Location -LiteralPath $WorkingDirectory
        try {
            $null = & $ExecutablePath @Arguments
        }
        finally {
            Pop-Location
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Assert-SafePayloadRelativePath {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or
        $RelativePath.Contains('"') -or
        $RelativePath.Contains('$')) {
        throw "Unsafe payload path in ${Context}: $RelativePath"
    }

    foreach ($segment in $RelativePath.Replace('/', '\').Split([char]'\')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "Unsafe payload path in ${Context}: $RelativePath"
        }
    }
}

function Get-DirectoryPayloadManifest {
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $manifest = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force | Sort-Object FullName) {
        $relative = (Get-RelativeChildPath -Parent $root -Child $entry.FullName).Replace('/', '\')
        Assert-SafePayloadRelativePath -RelativePath $relative -Context "directory $root"
        $kind = if ($entry.PSIsContainer) { 'D' } else { "F:$((Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash)" }
        if (-not $manifest.TryAdd($relative, $kind)) {
            throw "Payload manifest contains a case-insensitive duplicate: $relative"
        }
    }

    if ($manifest.Count -eq 0) {
        throw "Payload directory is empty: $root"
    }
    return $manifest
}

function Get-ArchivePayloadManifest {
    param(
        [Parameter(Mandatory)][string]$SevenZipPath,
        [Parameter(Mandatory)][string]$ArchivePath
    )

    $listing = & $SevenZipPath 'l' '-slt' '-sccUTF-8' $ArchivePath
    if ($LASTEXITCODE -ne 0) {
        throw "Listing payload archive failed with exit code $LASTEXITCODE."
    }

    $manifest = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $inEntries = $false
    $entryPath = $null
    $entryIsDirectory = $false
    foreach ($line in $listing) {
        if ($line -eq '----------') {
            $inEntries = $true
            continue
        }
        if (-not $inEntries) {
            continue
        }

        if ($line.StartsWith('Path = ', [System.StringComparison]::Ordinal)) {
            if ($null -ne $entryPath) {
                Assert-SafePayloadRelativePath -RelativePath $entryPath -Context "archive $ArchivePath"
                $kind = if ($entryIsDirectory) { 'D' } else { 'F' }
                if (-not $manifest.TryAdd($entryPath, $kind)) {
                    throw "Payload archive contains a duplicate path: $entryPath"
                }
            }
            $entryPath = $line.Substring('Path = '.Length).Replace('/', '\')
            $entryIsDirectory = $false
            continue
        }
        if ($line.StartsWith('Attributes = ', [System.StringComparison]::Ordinal)) {
            $entryIsDirectory = $line.Substring('Attributes = '.Length).Contains('D')
        }
    }

    if ($null -ne $entryPath) {
        Assert-SafePayloadRelativePath -RelativePath $entryPath -Context "archive $ArchivePath"
        $kind = if ($entryIsDirectory) { 'D' } else { 'F' }
        if (-not $manifest.TryAdd($entryPath, $kind)) {
            throw "Payload archive contains a duplicate path: $entryPath"
        }
    }
    if ($manifest.Count -eq 0) {
        throw "Payload archive contains no entries: $ArchivePath"
    }
    return $manifest
}

function Assert-PayloadManifestMatches {
    param(
        [Parameter(Mandatory)][System.Collections.Generic.Dictionary[string, string]]$Expected,
        [Parameter(Mandatory)][System.Collections.Generic.Dictionary[string, string]]$Actual,
        [Parameter(Mandatory)][string]$Context,
        [switch]$AllowFileHashes
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "Payload manifest count mismatch for $Context. Expected $($Expected.Count), found $($Actual.Count)."
    }
    foreach ($entry in $Expected.GetEnumerator()) {
        $actualValue = ''
        if (-not $Actual.TryGetValue($entry.Key, [ref]$actualValue)) {
            throw "Payload manifest is missing '$($entry.Key)' in $Context."
        }
        $expectedKind = if ($entry.Value.StartsWith('F:', [System.StringComparison]::Ordinal)) { 'F' } else { $entry.Value }
        $actualKind = if ($actualValue.StartsWith('F:', [System.StringComparison]::Ordinal)) { 'F' } else { $actualValue }
        if ($expectedKind -ne $actualKind) {
            throw "Payload manifest type mismatch for '$($entry.Key)' in $Context."
        }
        if ($AllowFileHashes -and $entry.Value -ne $actualValue) {
            throw "Payload manifest hash mismatch for '$($entry.Key)' in $Context."
        }
    }
}

function New-PayloadArchive {
    param(
        [Parameter(Mandatory)][string]$BuilderPath,
        [Parameter(Mandatory)][string]$ExtractorPath,
        [Parameter(Mandatory)][string]$PublishPath,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$ValidationPath
    )

    $expectedManifest = Get-DirectoryPayloadManifest -Path $PublishPath
    Write-Host "Compressing publish payload with 7-Zip LZMA2 multi-threading: $ArchivePath"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-CheckedExecutable -ExecutablePath $BuilderPath -Arguments @(
        'a', '-t7z', '-m0=lzma2', '-mmt=on', '-mx=5', '-bd', $ArchivePath, '*'
    ) -Operation '7-Zip payload compression' -WorkingDirectory $PublishPath
    $stopwatch.Stop()

    if (-not [System.IO.File]::Exists($ArchivePath) -or (Get-Item -LiteralPath $ArchivePath).Length -le 0) {
        throw "7-Zip did not produce a non-empty payload archive: $ArchivePath"
    }

    Invoke-CheckedExecutable -ExecutablePath $ExtractorPath -Arguments @('t', '-bd', $ArchivePath) -Operation '7-Zip payload integrity test'
    $archiveManifest = Get-ArchivePayloadManifest -SevenZipPath $BuilderPath -ArchivePath $ArchivePath
    Assert-PayloadManifestMatches -Expected $expectedManifest -Actual $archiveManifest -Context 'archive entries'

    Reset-GeneratedDirectory -Path $ValidationPath
    Invoke-CheckedExecutable -ExecutablePath $ExtractorPath -Arguments @('x', '-y', '-bd', "-o$ValidationPath", $ArchivePath) -Operation '7-Zip payload extraction'
    $extractedManifest = Get-DirectoryPayloadManifest -Path $ValidationPath
    Assert-PayloadManifestMatches -Expected $expectedManifest -Actual $extractedManifest -Context 'extracted payload' -AllowFileHashes

    return [pscustomobject]@{
        ArchivePath = $ArchivePath
        ArchiveSize = (Get-Item -LiteralPath $ArchivePath).Length
        CompressionElapsed = $stopwatch.Elapsed
    }
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
        'Call RemoveInstalledPayload',
        'Call DeployPayload',
        'taskkill.exe',
        'taskkill.exe" /F /IM "${PRODUCT_EXE}"',
        '!include "${UNINSTALL_FILES_INCLUDE}"',
        '!ifndef PAYLOAD_ARCHIVE',
        '!ifndef PAYLOAD_EXTRACTOR',
        'File /oname=payload.7z "${PAYLOAD_ARCHIVE}"',
        'File /oname=7zr.exe "${PAYLOAD_EXTRACTOR}"',
        '!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"',
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
    if ($source.IndexOf('File /r "${PUBLISH_DIR}\*.*"', [System.StringComparison]::Ordinal) -ge 0 -or
        $source.IndexOf('PUBLISH_DIR', [System.StringComparison]::Ordinal) -ge 0) {
        throw 'Installer must embed only the pre-compressed payload archive, not the publish directory.'
    }
    $coreStart = $source.IndexOf('Section "$(SectionProgram)"', [System.StringComparison]::Ordinal)
    $coreEnd = $source.IndexOf('SectionEnd', $coreStart, [System.StringComparison]::Ordinal)
    $core = $source.Substring($coreStart, $coreEnd - $coreStart)
    $replacementOrderPattern = [System.Text.RegularExpressions.Regex]::new(
        'Call EnsureAppClosed\s+Call RemoveInstalledPayload\s+Call DeployPayload',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $replacementOrderPattern.IsMatch($core)) {
        throw 'Installer must close the application and remove the current payload before replacing program files.'
    }
    $orderedCalls = @(
        'Call EnsureAppClosed',
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
    foreach ($forbidden in @(
        'Function BackupLedger',
        'preinstall-${PRODUCT_VERSION}',
        'usage ledger backup',
        'MUI_FINISHPAGE_RUN_NOTCHECKED',
        'taskkill.exe" /T'
    )) {
        if ($source.IndexOf($forbidden, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Installer must not retain ledger backup behavior: $forbidden"
        }
    }
    $deployStart = $source.IndexOf('Function DeployPayload', [System.StringComparison]::Ordinal)
    $deployEnd = $source.IndexOf('FunctionEnd', $deployStart, [System.StringComparison]::Ordinal)
    $deployPayload = $source.Substring($deployStart, $deployEnd - $deployStart)
    $deploySteps = @(
        'InitPluginsDir',
        'SetOutPath "$PLUGINSDIR"',
        'SetCompress off',
        'File /oname=payload.7z "${PAYLOAD_ARCHIVE}"',
        'SetCompress auto',
        'File /oname=7zr.exe "${PAYLOAD_EXTRACTOR}"',
        'IfFileExists "$PLUGINSDIR\payload.7z" 0 deploy_failed',
        'IfFileExists "$PLUGINSDIR\7zr.exe" 0 deploy_failed',
        'nsExec::ExecToStack ''"$PLUGINSDIR\7zr.exe" x -y -bd -o"$INSTDIR" "$PLUGINSDIR\payload.7z"''',
        'WriteUninstaller "$INSTDIR\${UNINSTALL_EXE}"'
    )
    $previousIndex = -1
    foreach ($step in $deploySteps) {
        $index = $deployPayload.IndexOf($step, [System.StringComparison]::Ordinal)
        if ($index -le $previousIndex) {
            throw "Installer payload deployment is invalid at: $step"
        }
        $previousIndex = $index
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

    foreach ($relativePath in @(
        'DirectML.dll',
        'onnxruntime.dll',
        'Microsoft.ML.OnnxRuntime.dll',
        'Microsoft.Windows.AI.MachineLearning.dll',
        'Microsoft.Windows.AI.MachineLearning.Projection.dll'
    )) {
        $path = Join-Path $PublishPath $relativePath
        if ([System.IO.File]::Exists($path)) {
            throw "Unused Windows AI/ML runtime asset was published: $path"
        }
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

$autoDetectedDependencies = $null
if ($AutoDetectDependencies) {
    $autoDetectedDependencies = Resolve-AutoDetectedPackagingDependencies `
        -BuilderPath $SevenZipPath `
        -RuntimePath $SevenZipRuntimePath `
        -SearchDirectories $DependencySearchDirectory
}

$dotnetExecutable = if ($AutoDetectDependencies) {
    $autoDetectedDependencies.DotnetPath
}
else {
    'dotnet'
}
$verifiedMakeNsisPath = if ($AutoDetectDependencies) {
    $autoDetectedDependencies.MakeNsisPath
}
else {
    $null
}

if ($ValidateOnly) {
    if ($AutoDetectDependencies) {
        Write-Host "Auto-detected packaging dependencies: dotnet=$dotnetExecutable; makensis=$verifiedMakeNsisPath; 7za=$($autoDetectedDependencies.SevenZipPath); 7zr=$($autoDetectedDependencies.SevenZipRuntimePath)"
    }
    Write-Host "Installer static validation passed: $installerScript"
    return
}
$sevenZipBuilder = if ($AutoDetectDependencies) {
    $autoDetectedDependencies.SevenZipPath
}
else {
    Resolve-RequiredExecutable -Path $SevenZipPath -ParameterName 'SevenZipPath' -Description 'The x64 7za.exe compression tool'
}
$sevenZipRuntime = if ($AutoDetectDependencies) {
    $autoDetectedDependencies.SevenZipRuntimePath
}
else {
    Resolve-RequiredExecutable -Path $SevenZipRuntimePath -ParameterName 'SevenZipRuntimePath' -Description 'The 7zr.exe extraction tool to embed in the installer'
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
    & $dotnetExecutable @publishArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $applicationExe = Join-Path $publishDirectory 'Codex Usage Desktop.exe'
    if (-not [System.IO.File]::Exists($applicationExe)) {
        throw "Publish did not produce the expected executable: $applicationExe"
    }

    Assert-WinUiPublish -PublishPath $publishDirectory -IconPath $appIconFile
    Write-UninstallManifest -PublishPath $publishDirectory -Destination $uninstallInclude
    $payload = New-PayloadArchive -BuilderPath $sevenZipBuilder -ExtractorPath $sevenZipRuntime -PublishPath $publishDirectory -ArchivePath $payloadArchive -ValidationPath $payloadValidationDirectory
    $extractedApplicationExe = Join-Path $payloadValidationDirectory 'Codex Usage Desktop.exe'
    if (-not [System.IO.File]::Exists($extractedApplicationExe)) {
        throw "Extracted payload did not produce the expected executable: $extractedApplicationExe"
    }
    Assert-WinUiPublish -PublishPath $payloadValidationDirectory -IconPath $appIconFile
    Write-Host "Payload archive size: $($payload.ArchiveSize) bytes"
    Write-Host "Payload compression elapsed: $($payload.CompressionElapsed)"

    $makeNsis = Find-MakeNsis -VerifiedPath $verifiedMakeNsisPath
    $fileVersion = "$Version.0"
    $makeNsisArguments = @(
        '/V3',
        '/WX',
        '/INPUTCHARSET', 'UTF8',
        "/DPRODUCT_VERSION=$Version",
        "/DPRODUCT_FILE_VERSION=$fileVersion",
        "/DPAYLOAD_ARCHIVE=$(Convert-ToNsisPath $payload.ArchivePath)",
        "/DPAYLOAD_EXTRACTOR=$(Convert-ToNsisPath $sevenZipRuntime)",
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
    Invoke-PublishedSmokeTest -ExecutablePath $extractedApplicationExe

    $published = Publish-InstallerSetup -PendingPath $pendingSetupPath -SetupPath $setupPath
    Write-Host "Setup: $($published.Path)"
    Write-Host "Size: $($published.Size) bytes"
    Write-Host "SHA256: $($published.SHA256)"

    [pscustomobject]@{
        Setup = $published.Path
        Size = $published.Size
        SHA256 = $published.SHA256
        MakeNsis = $makeNsis
        PayloadArchive = $payload.ArchivePath
        PayloadSize = $payload.ArchiveSize
        PayloadCompressionElapsed = $payload.CompressionElapsed
    }
}
finally {
    if ([System.IO.File]::Exists($pendingSetupPath)) {
        [System.IO.File]::Delete($pendingSetupPath)
    }
}
