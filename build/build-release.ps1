<#
.SYNOPSIS
    Builds the Snaplume release packages: portable zip, Inno Setup installer and SHA256 checksums.

.DESCRIPTION
    Works with PowerShell 7 and Windows PowerShell 5.1.

    1. Publishes src/Snaplume/Snaplume.csproj for win-x64, self-contained (users need no .NET install),
       ReadyToRun. Default layout "SingleFile": Snaplume.exe (single-file bundle) next to the few files that
       cannot be bundled: ScreenRecorderLib.dll (mixed-mode C++/CLI assembly, excluded from the bundle by
       build/SingleFile.targets) and WPF's native libraries (*_cor3.dll). "Folder" is the classic layout.
    2. Adds the Microsoft Visual C++ 2015-2022 x64 runtime needed by ScreenRecorderLib (C++/CLI):
       - "AppLocal": the redistributable DLLs from Visual Studio's VC\Redist\MSVC\<ver>\x64\Microsoft.VC14x.CRT
         folder are copied next to Snaplume.exe (app-local deployment, allowed by the Visual Studio
         redistribution terms). Both packages then work on a clean Windows without admin rights.
       - "Redist": vc_redist.x64.exe is downloaded (Microsoft-signed, checked) and embedded in the installer,
         which runs it only when the runtime is missing; the portable README tells users to install it.
       - "Auto" (default): AppLocal when a Visual Studio redist folder is found, Redist otherwise.
    3. Writes to the output folder (artifacts/ by default):
         Snaplume-<version>-portable.zip   folder "Snaplume" with the "portable" marker, LICENSE, README.txt
         Snaplume-Setup-<version>.exe      when Inno Setup 6 (ISCC.exe) is found, skipped otherwise
         SHA256SUMS.txt

.EXAMPLE
    pwsh build/build-release.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build-release.ps1 -Version 1.0.1 -VCRuntime AppLocal
#>
[CmdletBinding()]
param(
    # Repository root (defaults to the parent of this script's folder).
    [string]$Root,
    # Project to publish, relative to -Root or absolute.
    [string]$Project = 'src/Snaplume/Snaplume.csproj',
    # Version (e.g. 1.0.0 or v1.0.0); read from the project's <Version> when omitted.
    [string]$Version,
    # Output folder, relative to -Root or absolute.
    [string]$OutputDir = 'artifacts',
    [string]$Configuration = 'Release',
    # SingleFile: Snaplume.exe bundle + the files that cannot be bundled. Folder: classic multi-file publish.
    [ValidateSet('SingleFile', 'Folder')]
    [string]$Layout = 'SingleFile',
    [ValidateSet('Auto', 'AppLocal', 'Redist')]
    [string]$VCRuntime = 'Auto',
    # Folder containing vcruntime140.dll etc. (a VS "Microsoft.VC14x.CRT" folder), instead of searching for it.
    [string]$VCRedistDir,
    # Local vc_redist.x64.exe to embed instead of downloading it.
    [string]$VCRedistExe,
    # Path to ISCC.exe, instead of searching for it.
    [string]$IsccPath,
    [switch]$SkipInstaller,
    # Keep the intermediate folders (<OutputDir>\staging).
    [switch]$KeepStaging
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow in 5.1 with the progress bar

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn([string]$Message) {
    Write-Warning $Message
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning::$Message" }
}
function Resolve-FromRoot([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $script:Root $Path))
}
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    Write-Host "   $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $LASTEXITCODE." }
}
function Format-Size([long]$Bytes) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }

# --- Paths and tools ------------------------------------------------------------------------------

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
$ProjectPath = Resolve-FromRoot $Project
if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) { throw "Project not found: $ProjectPath" }
$OutputDir = Resolve-FromRoot $OutputDir
$Staging = Join-Path $OutputDir 'staging'
$LicenseFile = Join-Path $Root 'LICENSE'
if (-not (Test-Path -LiteralPath $LicenseFile)) { throw "LICENSE not found at $LicenseFile" }
$ReadmeTemplate = Join-Path $PSScriptRoot 'portable-readme.txt'
$SingleFileTargets = Join-Path $PSScriptRoot 'SingleFile.targets'
$IssFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\Snaplume.iss'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $Dotnet = $dotnet.Source }
else {
    $Dotnet = $null
    foreach ($candidate in @("$env:DOTNET_ROOT\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe", "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $Dotnet = $candidate; break }
    }
    if (-not $Dotnet) { throw 'The .NET SDK (dotnet) was not found. Install the .NET 10 SDK.' }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# --- Version --------------------------------------------------------------------------------------

if (-not $Version) {
    [xml]$csproj = Get-Content -LiteralPath $ProjectPath -Raw
    $node = $csproj.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node) { $node = $csproj.SelectSingleNode('/Project/PropertyGroup/VersionPrefix') }
    if (-not $node -or -not $node.InnerText.Trim()) { throw "No <Version> in $ProjectPath; pass -Version." }
    $Version = $node.InnerText.Trim()
}
$Version = $Version.Trim()
if ($Version.StartsWith('v') -or $Version.StartsWith('V')) { $Version = $Version.Substring(1) }
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Invalid version '$Version' (expected 1.2.3 or 1.2.3-beta.1)."
}
$NumericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

Write-Step "Snaplume $Version ($Layout, $Configuration, win-x64 self-contained)"
Write-Host "   Root:    $Root"
Write-Host "   Project: $ProjectPath"
Write-Host "   Output:  $OutputDir"
Write-Host "   dotnet:  $Dotnet ($(& $Dotnet --version))"

$ZipName = "Snaplume-$Version-portable.zip"
$SetupBaseName = "Snaplume-Setup-$Version"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($old in @($ZipName, "$SetupBaseName.exe", 'SHA256SUMS.txt')) {
    $oldPath = Join-Path $OutputDir $old
    if (Test-Path -LiteralPath $oldPath) { Remove-Item -LiteralPath $oldPath -Force }
}
if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
$PublishDir = Join-Path $Staging 'publish'
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

# --- Publish --------------------------------------------------------------------------------------

# Start from clean Release outputs: intermediate files of a previous publish with other settings
# (e.g. single-file vs folder) can be reused by the incremental build and produce a broken
# application. Only the bin\<Configuration> and obj\<Configuration> folders are removed.
$projectDir = Split-Path -Parent $ProjectPath
foreach ($dir in @((Join-Path $projectDir "bin\$Configuration"), (Join-Path $projectDir "obj\$Configuration"))) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# The interface languages are embedded resources; keep the framework (WPF/WinForms) satellite resources
# for the same languages only. Passed as an environment variable (MSBuild reads them as properties)
# because a ';' list cannot be passed reliably with -p.
$env:SatelliteResourceLanguages = 'en;fr;de;es;it;ar'

Write-Step 'Restore'
Invoke-Native $Dotnet @('restore', $ProjectPath, '-r', 'win-x64')

Write-Step 'Publish'
$publishArgs = @(
    'publish', $ProjectPath,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $PublishDir,
    "-p:Version=$Version",
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false'
)
if ($Layout -eq 'SingleFile') {
    $publishArgs += @(
        '-p:PublishSingleFile=true',
        # Native libraries (WPF's wpfgfx, PresentationNative, D3DCompiler...) stay next to the exe rather
        # than being bundled (IncludeNativeLibrariesForSelfExtract) and extracted to %TEMP% at first run:
        # no extraction delay, and nothing loaded from %TEMP% (often blocked on locked-down PCs).
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        # Keeps ScreenRecorderLib.dll (mixed-mode C++/CLI, cannot load from a bundle) next to Snaplume.exe.
        "-p:CustomAfterMicrosoftCommonTargets=$SingleFileTargets"
    )
}
try { Invoke-Native $Dotnet $publishArgs }
finally { Remove-Item Env:SatelliteResourceLanguages -ErrorAction SilentlyContinue }

# Symbols and IntelliSense files are not shipped (ScreenRecorderLib.pdb alone is 15 MB).
# (Where-Object rather than -Include: with -LiteralPath, Windows PowerShell 5.1 ignores -Include.)
Get-ChildItem -LiteralPath $PublishDir -Recurse -File |
    Where-Object { $_.Extension -eq '.pdb' -or $_.Extension -eq '.xml' } |
    Remove-Item -Force

foreach ($required in @('Snaplume.exe', 'ScreenRecorderLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $required))) {
        throw "Publish output is missing $required ($PublishDir)."
    }
}
if ($Layout -eq 'SingleFile') {
    # Next to the bundle: ScreenRecorderLib.dll and native libraries only (no managed assembly left out).
    $managed = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File |
        Where-Object { $_.Extension -eq '.dll' -and $_.Name -ne 'ScreenRecorderLib.dll' } |
        Where-Object {
            try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch { $false }
        })
    if ($managed.Count -gt 0) {
        Write-Warn "Managed assemblies left outside the single-file bundle: $(($managed | ForEach-Object Name) -join ', ')"
    }
    Write-Host "   Files: $((Get-ChildItem -LiteralPath $PublishDir -File | ForEach-Object Name) -join ', ')"
}

# --- Visual C++ runtime ---------------------------------------------------------------------------

function Find-VCRedistCrtDir {
    $roots = New-Object System.Collections.Generic.List[string]
    if ($env:VCToolsRedistDir) { $roots.Add($env:VCToolsRedistDir) }   # VS developer prompt
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        foreach ($vs in @(& $vswhere -all -prerelease -products * -property installationPath)) {
            if ($vs) { $roots.Add((Join-Path $vs 'VC\Redist\MSVC')) }
        }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        foreach ($year in @(Get-ChildItem -LiteralPath (Join-Path $base 'Microsoft Visual Studio') -Directory -ErrorAction SilentlyContinue)) {
            foreach ($edition in @(Get-ChildItem -LiteralPath $year.FullName -Directory -ErrorAction SilentlyContinue)) {
                $roots.Add((Join-Path $edition.FullName 'VC\Redist\MSVC'))
            }
        }
    }
    $found = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $r)) { continue }
        # Either ...\Redist\MSVC (versions below) or ...\Redist\MSVC\<version>\ (VCToolsRedistDir).
        $versionDirs = @(Get-ChildItem -LiteralPath $r -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' })
        if ($versionDirs.Count -eq 0 -and (Split-Path -Leaf $r.TrimEnd('\')) -match '^\d+\.\d+\.\d+$') { $versionDirs = @(Get-Item -LiteralPath $r) }
        foreach ($v in $versionDirs) {
            foreach ($crt in @(Get-ChildItem -LiteralPath (Join-Path $v.FullName 'x64') -Directory -Filter 'Microsoft.VC14*.CRT' -ErrorAction SilentlyContinue)) {
                if (Test-Path -LiteralPath (Join-Path $crt.FullName 'vcruntime140_1.dll')) {
                    $found += [pscustomobject]@{ Version = [version]$v.Name; Path = $crt.FullName }
                }
            }
        }
    }
    if ($found.Count -eq 0) { return $null }
    return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path
}

Write-Step "Visual C++ runtime ($VCRuntime)"
$CrtDir = $null
if ($VCRuntime -ne 'Redist') {
    if ($VCRedistDir) {
        $CrtDir = [IO.Path]::GetFullPath($VCRedistDir)
        if (-not (Test-Path -LiteralPath (Join-Path $CrtDir 'vcruntime140_1.dll'))) { throw "vcruntime140_1.dll not found in $CrtDir" }
    }
    else { $CrtDir = Find-VCRedistCrtDir }
    if ($CrtDir) {
        # ScreenRecorderLib 7.0.1 is linked with the 14.44 toolset: an older runtime is not supported.
        $crtVersion = (Get-Item -LiteralPath (Join-Path $CrtDir 'vcruntime140.dll')).VersionInfo
        if ([version]('{0}.{1}' -f $crtVersion.FileMajorPart, $crtVersion.FileMinorPart) -lt [version]'14.44') {
            $message = "The Visual C++ runtime in $CrtDir is $($crtVersion.FileVersion), older than the required 14.44."
            if ($VCRuntime -eq 'AppLocal') { throw $message }
            Write-Warn $message
            $CrtDir = $null
        }
    }
    if (-not $CrtDir -and $VCRuntime -eq 'AppLocal') {
        throw 'No Visual Studio "VC\Redist\MSVC\<version>\x64\Microsoft.VC14x.CRT" folder found (install the "MSVC C++ x64/x86 build tools" component, or pass -VCRedistDir).'
    }
}
if ($CrtDir) {
    $RuntimeMode = 'AppLocal'
    Write-Host "   App-local deployment from $CrtDir"
    $crtFiles = @(Get-ChildItem -LiteralPath $CrtDir -File -Filter '*.dll')
    foreach ($needed in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'concrt140.dll')) {
        if (-not ($crtFiles | Where-Object Name -eq $needed)) { throw "$needed missing from $CrtDir" }
    }
    $crtFiles | Copy-Item -Destination $PublishDir -Force
    Write-Host "   Copied: $(($crtFiles | ForEach-Object Name) -join ', ') ($((Get-Item -LiteralPath (Join-Path $CrtDir 'vcruntime140.dll')).VersionInfo.FileVersion))"
}
else {
    $RuntimeMode = 'Redist'
    Write-Warn 'Visual C++ redistributable DLLs not found: the installer will embed vc_redist.x64.exe and the portable zip will require the Visual C++ 2015-2022 x64 runtime.'
}

# --- Portable zip ---------------------------------------------------------------------------------

Write-Step "Portable package ($ZipName)"
$PortableDir = Join-Path $Staging 'portable\Snaplume'
New-Item -ItemType Directory -Force -Path $PortableDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $PortableDir -Recurse -Force
New-Item -ItemType File -Force -Path (Join-Path $PortableDir 'portable') | Out-Null
Copy-Item -LiteralPath $LicenseFile -Destination (Join-Path $PortableDir 'LICENSE') -Force

# README.txt from build/portable-readme.txt (UTF-8; this script stays ASCII-only because Windows
# PowerShell 5.1 reads BOM-less scripts in the ANSI code page). Lines prefixed with [[applocal]] or
# [[redist]] are kept only for the matching Visual C++ runtime strategy.
$keep = '[[' + $RuntimeMode.ToLowerInvariant() + ']]'
$readmeLines = foreach ($line in ([IO.File]::ReadAllText($ReadmeTemplate, [Text.Encoding]::UTF8) -split "`r?`n")) {
    if ($line.StartsWith($keep)) { $line.Substring($keep.Length) }
    elseif ($line -notmatch '^\[\[(applocal|redist)\]\]') { $line.Replace('{{VERSION}}', $Version) }
}
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $PortableDir 'README.txt'), (($readmeLines -join "`r`n").TrimEnd() + "`r`n"), $utf8Bom)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ZipPath = Join-Path $OutputDir $ZipName
$portableRoot = Split-Path -Parent $PortableDir
$stream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $PortableDir -Recurse -File | Sort-Object FullName)) {
            # Forward slashes: Compress-Archive in 5.1 writes backslashes, which other unzip tools mishandle.
            $entryName = $file.FullName.Substring($portableRoot.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

# --- Installer ------------------------------------------------------------------------------------

function Find-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path -LiteralPath $IsccPath)) { throw "ISCC.exe not found at $IsccPath" }
        return [IO.Path]::GetFullPath($IsccPath)
    }
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($item -and ($item.PSObject.Properties.Name -contains 'InstallLocation') -and $item.InstallLocation) {
            $candidates.Add((Join-Path $item.InstallLocation 'ISCC.exe'))
        }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $base) { continue }
        $candidates.Add((Join-Path $base 'Inno Setup 6\ISCC.exe'))
        foreach ($dir in @(Get-ChildItem -LiteralPath $base -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
            $candidates.Add((Join-Path $dir.FullName 'ISCC.exe'))
        }
    }
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}

$SetupPath = $null
if ($SkipInstaller) {
    Write-Step 'Installer skipped (-SkipInstaller)'
}
else {
    $iscc = Find-Iscc
    if (-not $iscc) {
        Write-Warn 'Inno Setup 6 (ISCC.exe) not found: the installer was NOT built. Install it from https://jrsoftware.org/isdl.php (or "choco install innosetup") and run this script again.'
    }
    else {
        Write-Step "Installer ($SetupBaseName.exe)"
        $isccArgs = @(
            '/Qp',
            "/DAppVersion=$Version",
            "/DAppVersionNumeric=$NumericVersion",
            "/DSourceDir=$PublishDir",
            "/DRepoRoot=$Root",
            "/DOutputDir=$OutputDir",
            "/DOutputBaseName=$SetupBaseName"
        )
        if ($RuntimeMode -eq 'Redist') {
            if ($VCRedistExe) {
                $redistPath = [IO.Path]::GetFullPath($VCRedistExe)
            }
            else {
                $redistPath = Join-Path $Staging 'redist\vc_redist.x64.exe'
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $redistPath) | Out-Null
                Write-Host '   Downloading https://aka.ms/vs/17/release/vc_redist.x64.exe'
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redistPath -UseBasicParsing
            }
            $sig = Get-AuthenticodeSignature -LiteralPath $redistPath
            $signer = ''
            if ($sig.SignerCertificate) { $signer = $sig.SignerCertificate.Subject }
            if ($sig.Status -ne 'Valid' -or $signer -notmatch 'O=Microsoft Corporation') {
                throw "vc_redist.x64.exe signature check failed (status $($sig.Status), signer '$signer')."
            }
            Write-Host "   vc_redist.x64.exe $((Get-Item -LiteralPath $redistPath).VersionInfo.FileVersion), signed by Microsoft"
            $isccArgs += "/DVCRedistExe=$redistPath"
        }
        $isccArgs += $IssFile
        Invoke-Native $iscc $isccArgs
        $SetupPath = Join-Path $OutputDir "$SetupBaseName.exe"
        if (-not (Test-Path -LiteralPath $SetupPath)) { throw "ISCC succeeded but $SetupPath is missing." }
    }
}

# --- Checksums ------------------------------------------------------------------------------------

Write-Step 'SHA256SUMS.txt'
$packages = @($ZipPath)
if ($SetupPath) { $packages += $SetupPath }
$lines = foreach ($p in $packages) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $p)
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $OutputDir 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), $utf8NoBom)

if (-not $KeepStaging) { Remove-Item -LiteralPath $Staging -Recurse -Force }

Write-Step 'Done'
foreach ($p in $packages + @(Join-Path $OutputDir 'SHA256SUMS.txt')) {
    Write-Host ('   {0,-40} {1,10}' -f (Split-Path -Leaf $p), (Format-Size (Get-Item -LiteralPath $p).Length))
}
Write-Host "   VC++ runtime: $RuntimeMode"
if ($env:GITHUB_OUTPUT) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding ascii -Value @(
        "version=$Version",
        "zip=$ZipPath",
        "setup=$SetupPath",
        "sums=$(Join-Path $OutputDir 'SHA256SUMS.txt')",
        "vcruntime=$RuntimeMode"
    )
}
