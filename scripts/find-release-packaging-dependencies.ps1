#requires -PSEdition Core
#requires -Version 7.4

[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$SevenZipPath,

    [string]$SevenZipRuntimePath,

    [string[]]$SearchDirectory = @(),

    [switch]$RequireAll,

    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-SearchDirectories {
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

function Test-ExpectedExecutableFile {
    param(
        [Parameter(Mandatory)]
        [string]$CandidatePath,

        [Parameter(Mandatory)]
        [string]$ExpectedFileName
    )

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $null
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($CandidatePath)
    }
    catch {
        return $null
    }

    if (-not [string]::Equals(
        [System.IO.Path]::GetFileName($fullPath),
        $ExpectedFileName,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    if (-not [System.IO.File]::Exists($fullPath)) {
        return $null
    }

    try {
        $file = Get-Item -LiteralPath $fullPath -Force
    }
    catch {
        return $null
    }

    if ($file -isnot [System.IO.FileInfo] -or $file.Length -le 0) {
        return $null
    }

    return $file.FullName
}

function Test-ExecutableCanRun {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    try {
        & $Path @Arguments *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Get-PathApplicationCandidate {
    param(
        [Parameter(Mandatory)]
        [string]$FileName
    )

    $command = Get-Command -Name $FileName -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command -or [string]::IsNullOrWhiteSpace($command.Path)) {
        return $null
    }

    return [string]$command.Path
}

function Get-DirectSearchCandidates {
    param(
        [AllowEmptyCollection()]
        [string[]]$Directories = @(),

        [Parameter(Mandatory)]
        [string]$FileName
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($directory in $Directories) {
        if ([string]::IsNullOrWhiteSpace($directory)) {
            continue
        }

        try {
            $fullDirectory = [System.IO.Path]::GetFullPath($directory)
        }
        catch {
            continue
        }

        if ([System.IO.Directory]::Exists($fullDirectory)) {
            $candidates.Add((Join-Path $fullDirectory $FileName))
        }
    }

    return $candidates.ToArray()
}

function Find-AvailableSevenZipExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [AllowEmptyCollection()]
        [string[]]$Directories = @()
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    $pathCandidate = Get-PathApplicationCandidate -FileName $FileName
    if ($null -ne $pathCandidate) {
        $candidates.Add($pathCandidate)
    }

    foreach ($candidate in (Get-DirectSearchCandidates -Directories $Directories -FileName $FileName)) {
        $candidates.Add($candidate)
    }

    $fixedDirectories = [System.Collections.Generic.List[string]]::new()
    $fixedDirectories.Add('C:\Tools\7-Zip')
    foreach ($programFilesDirectory in @(
        [System.Environment]::GetEnvironmentVariable('ProgramFiles'),
        [System.Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($programFilesDirectory)) {
            $fixedDirectories.Add((Join-Path $programFilesDirectory '7-Zip'))
        }
    }

    foreach ($directory in $fixedDirectories) {
        $candidates.Add((Join-Path $directory $FileName))
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        $executablePath = Test-ExpectedExecutableFile -CandidatePath $candidate -ExpectedFileName $FileName
        if ($null -eq $executablePath -or -not $seen.Add($executablePath)) {
            continue
        }

        if (Test-ExecutableCanRun -Path $executablePath -Arguments @('i')) {
            return $executablePath
        }
    }

    return $null
}

function Resolve-ExplicitSevenZipExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ParameterName,

        [Parameter(Mandatory)]
        [string]$ExpectedFileName
    )

    $executablePath = Test-ExpectedExecutableFile -CandidatePath $Path -ExpectedFileName $ExpectedFileName
    if ($null -eq $executablePath) {
        throw "-$ParameterName must be an existing, non-empty $ExpectedFileName file: $Path"
    }

    if (-not (Test-ExecutableCanRun -Path $executablePath -Arguments @('i'))) {
        throw "-$ParameterName is not executable: $executablePath"
    }

    return $executablePath
}

function Get-DotnetTenSdkVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $sdkLines = @(& $Path '--list-sdks' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            return $null
        }
    }
    catch {
        return $null
    }

    $versions = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $sdkLines) {
        $match = [System.Text.RegularExpressions.Regex]::Match(
            [string]$line,
            '^\s*(?<version>10\.\d+\.\d+(?:[-+][^\s]+)?)\s+\[')
        if ($match.Success) {
            $versions.Add($match.Groups['version'].Value)
        }
    }

    if ($versions.Count -eq 0) {
        return $null
    }

    return $versions[$versions.Count - 1]
}

function Get-DotnetAvailability {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $pathCandidate = Get-PathApplicationCandidate -FileName 'dotnet.exe'
    if ($null -ne $pathCandidate) {
        $candidates.Add($pathCandidate)
    }
    $programFilesDirectory = [System.Environment]::GetEnvironmentVariable('ProgramFiles')
    if (-not [string]::IsNullOrWhiteSpace($programFilesDirectory)) {
        $candidates.Add((Join-Path $programFilesDirectory 'dotnet\dotnet.exe'))
    }

    $firstAvailablePath = $null
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        $executablePath = Test-ExpectedExecutableFile -CandidatePath $candidate -ExpectedFileName 'dotnet.exe'
        if ($null -eq $executablePath -or -not $seen.Add($executablePath)) {
            continue
        }

        if ($null -eq $firstAvailablePath) {
            $firstAvailablePath = $executablePath
        }

        $sdkVersion = Get-DotnetTenSdkVersion -Path $executablePath
        if ($null -ne $sdkVersion) {
            return [pscustomobject]@{
                Path = $executablePath
                SdkVersion = $sdkVersion
            }
        }
    }

    return [pscustomobject]@{
        Path = $firstAvailablePath
        SdkVersion = $null
    }
}

function Get-CompatibleMakeNsis {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $pathCandidate = Get-PathApplicationCandidate -FileName 'makensis.exe'
    if ($null -ne $pathCandidate) {
        $candidates.Add($pathCandidate)
    }

    foreach ($programFilesDirectory in @(
        [System.Environment]::GetEnvironmentVariable('ProgramFiles(x86)'),
        [System.Environment]::GetEnvironmentVariable('ProgramFiles')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($programFilesDirectory)) {
            $candidates.Add((Join-Path $programFilesDirectory 'NSIS\makensis.exe'))
        }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        $executablePath = Test-ExpectedExecutableFile -CandidatePath $candidate -ExpectedFileName 'makensis.exe'
        if ($null -eq $executablePath -or -not $seen.Add($executablePath)) {
            continue
        }

        try {
            $versionOutput = @(& $executablePath '/VERSION' 2>&1)
            if ($LASTEXITCODE -ne 0) {
                continue
            }
        }
        catch {
            continue
        }

        $versionMatch = [System.Text.RegularExpressions.Regex]::Match(
            ($versionOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine,
            '(?<!\d)(?<version>3\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.]+)?)(?!\d)')
        if (-not $versionMatch.Success) {
            continue
        }

        return [pscustomobject]@{
            Path = $executablePath
            Version = $versionMatch.Groups['version'].Value
        }
    }

    return [pscustomobject]@{
        Path = $null
        Version = $null
    }
}

$powerShellPath = Test-ExpectedExecutableFile -CandidatePath ([System.Environment]::ProcessPath) -ExpectedFileName 'pwsh.exe'
if ($null -eq $powerShellPath) {
    throw 'The current PowerShell Core host could not be resolved to pwsh.exe.'
}

$dotnet = Get-DotnetAvailability
$dotnetPath = $dotnet.Path
$dotnetSdkVersion = $dotnet.SdkVersion

$makeNsis = Get-CompatibleMakeNsis
$searchDirectories = [string[]]@(
    ConvertTo-SearchDirectories -Directories $SearchDirectory
)
$sevenZipBuilder = if ([string]::IsNullOrWhiteSpace($SevenZipPath)) {
    Find-AvailableSevenZipExecutable -FileName '7za.exe' -Directories $searchDirectories
}
else {
    Resolve-ExplicitSevenZipExecutable -Path $SevenZipPath -ParameterName 'SevenZipPath' -ExpectedFileName '7za.exe'
}
$sevenZipRuntime = if ([string]::IsNullOrWhiteSpace($SevenZipRuntimePath)) {
    Find-AvailableSevenZipExecutable -FileName '7zr.exe' -Directories $searchDirectories
}
else {
    Resolve-ExplicitSevenZipExecutable -Path $SevenZipRuntimePath -ParameterName 'SevenZipRuntimePath' -ExpectedFileName '7zr.exe'
}

$missing = [System.Collections.Generic.List[string]]::new()
if ($null -eq $dotnetPath) {
    $missing.Add('dotnet.exe')
}
elseif ($null -eq $dotnetSdkVersion) {
    $missing.Add('.NET 10 SDK')
}
if ($null -eq $makeNsis.Path) {
    $missing.Add('makensis.exe (NSIS 3.x)')
}
if ($null -eq $sevenZipBuilder) {
    $missing.Add('7za.exe')
}
if ($null -eq $sevenZipRuntime) {
    $missing.Add('7zr.exe')
}

$result = [pscustomobject][ordered]@{
    Ready = $missing.Count -eq 0
    PowerShellPath = $powerShellPath
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    DotnetPath = $dotnetPath
    DotnetSdkVersion = $dotnetSdkVersion
    MakeNsisPath = $makeNsis.Path
    MakeNsisVersion = $makeNsis.Version
    SevenZipPath = $sevenZipBuilder
    SevenZipRuntimePath = $sevenZipRuntime
    Missing = $missing.ToArray()
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 3 -Compress
}
else {
    $result
}

if ($RequireAll -and -not $result.Ready) {
    throw "Release packaging dependencies are missing or incompatible: $($result.Missing -join ', ')"
}
