<#
.SYNOPSIS
    Builds the Snaplume Linux packages: .deb and .rpm for x64 and arm64, with SHA256 checksums.

.DESCRIPTION
    Works with PowerShell 7 (Windows, Linux, macOS) and Windows PowerShell 5.1.

    1. Publishes src/Snaplume.App (net10.0) for linux-x64 and linux-arm64: self-contained (users need no .NET
       install), single executable "Snaplume", plus the native libraries a single file cannot carry
       (libSkiaSharp.so, libHarfBuzzSharp.so).
    2. Stages the package tree in <OutputDir>/staging/<arch>:
         opt/snaplume/                         Snaplume, *.so, LICENSE
         share/applications/                   io.github.mrddream.Snaplume.desktop
         share/metainfo/                       io.github.mrddream.Snaplume.metainfo.xml
         share/icons/hicolor/<size>/apps/      PNG icons (packaging/linux/icons) and the scalable SVG
         scripts/                              postinstall.sh, postremove.sh
       Text files get Unix line endings whatever the checkout.
    3. Runs nfpm (packaging/linux/nfpm.yaml) for .deb and .rpm, then writes SHA256SUMS.
       nfpm is not downloaded by this script: it is taken from -NfpmPath, PATH, or <Root>/artifacts/tools.
       Without it, the staging is kept and the script explains where to get it.

    Output (artifacts/linux by default):
         snaplume_<version>-1_amd64.deb     snaplume-<version>-1.x86_64.rpm
         snaplume_<version>-1_arm64.deb     snaplume-<version>-1.aarch64.rpm
         SHA256SUMS

.EXAMPLE
    pwsh build/build-linux.ps1
.EXAMPLE
    pwsh build/build-linux.ps1 -Architectures x64 -NfpmPath C:\tools\nfpm.exe
.EXAMPLE
    pwsh build/build-linux.ps1 -SkipPackages      # publish and stage only
#>
[CmdletBinding()]
param(
    # Repository root (defaults to the parent of this script's folder).
    [string]$Root,
    # Project to publish, relative to -Root or absolute.
    [string]$Project = 'src/Snaplume.App/Snaplume.App.csproj',
    # Version (e.g. 1.2.0 or v1.2.0); read from Directory.Build.props when omitted.
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architectures = @('x64', 'arm64'),
    # Output folder, relative to -Root or absolute.
    [string]$OutputDir = 'artifacts/linux',
    [string]$Configuration = 'Release',
    # Path to the nfpm executable, instead of searching PATH and artifacts/tools.
    [string]$NfpmPath,
    # Publish and stage only; no .deb/.rpm.
    [switch]$SkipPackages,
    # Keep <OutputDir>/staging after building the packages.
    [switch]$KeepStaging
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

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

$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# Copies a text file with LF line endings and no BOM (desktop files, scripts and XML read by Linux tools).
function Copy-UnixText([string]$Source, [string]$Destination) {
    $text = [IO.File]::ReadAllText($Source)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $text = $text.Replace("`r`n", "`n")
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    [IO.File]::WriteAllText($Destination, $text, $script:Utf8NoBom)
}

function Copy-Binary([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Find-Nfpm {
    if ($NfpmPath) {
        if (-not (Test-Path -LiteralPath $NfpmPath -PathType Leaf)) { throw "nfpm not found at $NfpmPath" }
        return [IO.Path]::GetFullPath($NfpmPath)
    }
    $command = Get-Command nfpm -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $tools = Join-Path $Root 'artifacts/tools'
    foreach ($candidate in @('nfpm.exe', 'nfpm', 'nfpm/nfpm.exe', 'nfpm/nfpm')) {
        $path = Join-Path $tools $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) { return [IO.Path]::GetFullPath($path) }
    }
    return $null
}

# --- Paths, version ---------------------------------------------------------------------------------

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
$ProjectPath = Resolve-FromRoot $Project
if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) { throw "Project not found: $ProjectPath" }
$OutputDir = Resolve-FromRoot $OutputDir
$Staging = Join-Path $OutputDir 'staging'
$Packaging = Join-Path $Root 'packaging/linux'
$NfpmConfig = Join-Path $Packaging 'nfpm.yaml'
$LicenseFile = Join-Path $Root 'LICENSE'
$Logo = Join-Path $Root 'docs/branding/snaplume.svg'
foreach ($required in @($NfpmConfig, $LicenseFile, $Logo)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing file: $required" }
}

if (-not $Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('//Version')
    if (-not $node) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
    $Version = $node.InnerText.Trim()
}
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version '$Version' (expected 1.2.3)." }

# The AppStream release list must announce this version, or software centres show an outdated changelog.
[xml]$metainfo = Get-Content -LiteralPath (Join-Path $Packaging 'io.github.mrddream.Snaplume.metainfo.xml') -Raw -Encoding UTF8
$latestRelease = $metainfo.SelectSingleNode('//releases/release[1]')
if (-not $latestRelease -or $latestRelease.GetAttribute('version') -ne $Version) {
    Write-Warn "packaging/linux/io.github.mrddream.Snaplume.metainfo.xml has no <release version=""$Version""> first in <releases>."
}

Write-Step "Snaplume $Version for Linux ($($Architectures -join ', '))"
if (Test-Path -LiteralPath $Staging) { Remove-Item -LiteralPath $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$iconSizes = @('16x16', '22x22', '24x24', '32x32', '48x48', '64x64', '128x128', '256x256', '512x512')
$iconName = 'io.github.mrddream.Snaplume'

# --- Publish and stage ------------------------------------------------------------------------------

foreach ($arch in $Architectures) {
    $rid = "linux-$arch"
    $archStaging = Join-Path $Staging $arch
    $publish = Join-Path $archStaging 'publish'
    Write-Step "Publishing $rid"
    Invoke-Native $dotnet @(
        'publish', $ProjectPath,
        '-f', 'net10.0',
        '-r', $rid,
        '-c', $Configuration,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        '-o', $publish
    )

    $executable = Join-Path $publish 'Snaplume'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "The publish produced no 'Snaplume' executable in $publish." }
    $libraries = @(Get-ChildItem -LiteralPath $publish -File -Filter '*.so')
    # nfpm.yaml packages the executable and *.so only: anything else would be silently left out.
    $unexpected = @(Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -ne 'Snaplume' -and $_.Extension -ne '.so' })
    if ($unexpected.Count -gt 0) {
        throw "Unexpected files in the publish output (add them to packaging/linux/nfpm.yaml): $($unexpected.Name -join ', ')"
    }

    Write-Step "Staging $arch"
    $opt = Join-Path $archStaging 'opt/snaplume'
    Copy-Binary $executable (Join-Path $opt 'Snaplume')
    foreach ($library in $libraries) { Copy-Binary $library.FullName (Join-Path $opt $library.Name) }
    Copy-UnixText $LicenseFile (Join-Path $opt 'LICENSE')
    Copy-UnixText (Join-Path $Packaging "$iconName.desktop") (Join-Path $archStaging "share/applications/$iconName.desktop")
    Copy-UnixText (Join-Path $Packaging "$iconName.metainfo.xml") (Join-Path $archStaging "share/metainfo/$iconName.metainfo.xml")
    foreach ($size in $iconSizes) {
        $png = Join-Path $Packaging "icons/hicolor/$size/apps/$iconName.png"
        if (-not (Test-Path -LiteralPath $png -PathType Leaf)) { throw "Missing icon $png (run: dotnet run --project packaging/linux/icongen)" }
        Copy-Binary $png (Join-Path $archStaging "share/icons/hicolor/$size/apps/$iconName.png")
    }
    Copy-UnixText $Logo (Join-Path $archStaging "share/icons/hicolor/scalable/apps/$iconName.svg")
    foreach ($scriptName in @('postinstall.sh', 'postremove.sh')) {
        Copy-UnixText (Join-Path $Packaging "scripts/$scriptName") (Join-Path $archStaging "scripts/$scriptName")
    }
    Remove-Item -LiteralPath $publish -Recurse -Force
    $size = (Get-ChildItem -LiteralPath $opt -File | Measure-Object -Property Length -Sum).Sum
    Write-Host "   /opt/snaplume: $(Format-Size $size) ($($libraries.Count) native libraries: $($libraries.Name -join ', '))"
}

if ($SkipPackages) {
    Write-Step "Staging ready in $Staging (-SkipPackages: no .deb/.rpm)."
    return
}

# --- Packages ---------------------------------------------------------------------------------------

$nfpm = Find-Nfpm
if (-not $nfpm) {
    Write-Warn @"
nfpm was not found, so no .deb/.rpm was built (the staging is kept in $Staging).
nfpm (MIT) is a single executable: download the archive for your system from
    https://github.com/goreleaser/nfpm/releases
check it against the release's checksums.txt, and put nfpm (nfpm.exe on Windows) in
    $(Join-Path $Root 'artifacts/tools')
or anywhere in PATH, or pass -NfpmPath. Then run this script again.
"@
    exit 1
}
Write-Host "   nfpm: $nfpm"

$debArch = @{ 'x64' = 'amd64'; 'arm64' = 'arm64' }
# Packages of an earlier run would end up in SHA256SUMS next to the new ones.
Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Extension -in '.deb', '.rpm' -or $_.Name -eq 'SHA256SUMS' } |
    Remove-Item -Force
$previousVersion = $env:SNAPLUME_VERSION
$previousArch = $env:SNAPLUME_ARCH
try {
    foreach ($arch in $Architectures) {
        $env:SNAPLUME_VERSION = $Version
        $env:SNAPLUME_ARCH = $debArch[$arch]
        Push-Location (Join-Path $Staging $arch)
        try {
            foreach ($packager in @('deb', 'rpm')) {
                Write-Step "Packaging $packager ($arch)"
                Invoke-Native $nfpm @('package', '--config', $NfpmConfig, '--packager', $packager, '--target', $OutputDir)
            }
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    $env:SNAPLUME_VERSION = $previousVersion
    $env:SNAPLUME_ARCH = $previousArch
}

Write-Step 'SHA256SUMS'
$packages = @(Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Extension -in '.deb', '.rpm' } | Sort-Object Name)
$lines = foreach ($package in $packages) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $package.Name
}
[IO.File]::WriteAllText((Join-Path $OutputDir 'SHA256SUMS'), (($lines -join "`n") + "`n"), $Utf8NoBom)

if (-not $KeepStaging) { Remove-Item -LiteralPath $Staging -Recurse -Force }

Write-Step 'Done'
foreach ($package in $packages) { Write-Host ('   {0,-44} {1}' -f $package.Name, (Format-Size $package.Length)) }
Write-Host "   SHA256SUMS"
