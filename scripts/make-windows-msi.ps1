<#
.SYNOPSIS
    Builds Invoklet-win-x64.msi end to end (publish + WiX build) for local use - mirrors the CI recipe
    in .github/workflows/dotnet-desktop.yml's build-windows job (installer/windows/Package.wxs), as a
    standalone script so it doesn't have to be pieced back together by hand every time. Unlike
    scripts/make-linux-*.sh/make-macos-app-bundle.sh (which wrap an ALREADY-published output, since CI
    calls them as a separate step), this one does its own `dotnet publish` too, since it's meant to be
    run directly by a person, not composed into a multi-step CI job.

.PARAMETER OutputPath
    Directory to write Invoklet-win-x64.msi into. Defaults to the repo root.

.PARAMETER SkipPublish
    Reuse whatever is already in publish/win-x64 instead of doing a fresh `dotnet publish` - faster
    for iterating on installer/windows/Package.wxs itself, but produces a STALE installer if the app's
    own source changed since that publish (this is exactly the bug that shipped a stale MSI once
    already - see installer/windows/Package.wxs's own git history) - off by default on purpose.

.EXAMPLE
    .\scripts\make-windows-msi.ps1
    Publishes Invoklet fresh and writes Invoklet-win-x64.msi to the repo root.

.EXAMPLE
    .\scripts\make-windows-msi.ps1 -SkipPublish
    Rebuilds the MSI from whatever's already in publish\win-x64 - use only right after a prior full run.
#>
param(
    [string]$OutputPath,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# Run from repo root regardless of the caller's own working directory - same convention as
# scripts/make-linux-*.sh/make-macos-app-bundle.sh's own "run from the repo root" header comments.
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not $OutputPath) { $OutputPath = $repoRoot }
if (-not (Test-Path $OutputPath)) { New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null }

$publishDir = "publish/win-x64"

if (-not $SkipPublish) {
    Write-Host "==> Publishing Invoklet (win-x64, self-contained)..." -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish src/main/Invoklet/Invoklet.csproj -c Release -r win-x64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} elseif (-not (Test-Path "$publishDir/Invoklet.exe")) {
    throw "-SkipPublish was passed but $publishDir/Invoklet.exe doesn't exist yet - run once without -SkipPublish first."
}

# WiX Toolset v5 CLI - installed as a per-user global .NET tool the first time this script runs.
# Pinned to v5 specifically: v6+ added an Open Source Maintenance Fee EULA-acceptance gate (harmless
# for a $0-revenue project - the fee itself only applies above $10k/year - but v5 needs no
# EULA-acceptance mechanism at all). See installer/windows/Package.wxs's own header comment.
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installing WiX Toolset CLI (one-time)..." -ForegroundColor Cyan
    dotnet tool install --global wix --version 5.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the wix global tool (exit $LASTEXITCODE)" }
    $env:PATH = "$(Join-Path $env:USERPROFILE '.dotnet\tools');$env:PATH"
}

# --global here (not the plain per-directory cache `wix extension add` defaults to) so this script
# behaves the same regardless of which directory it's invoked from - a per-directory cache would only
# ever be found again if every future run happened to use the exact same working directory.
$installedExtensions = & wix extension list --global 2>$null
if (-not ($installedExtensions -match "WixToolset\.UI\.wixext")) {
    Write-Host "==> Installing the WixToolset.UI.wixext extension (one-time)..." -ForegroundColor Cyan
    wix extension add --global WixToolset.UI.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the WixToolset.UI.wixext extension (exit $LASTEXITCODE)" }
}

# Version comes from Invoklet.csproj itself - the same source of truth CI reads (via a regex in the
# YAML); a real XML parse here since this is PowerShell, not bash.
[xml]$csprojXml = Get-Content "src/main/Invoklet/Invoklet.csproj"
$version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Could not read <Version> from src/main/Invoklet/Invoklet.csproj" }

$msiPath = Join-Path $OutputPath "Invoklet-win-x64.msi"
Write-Host "==> Building $msiPath (version $version)..." -ForegroundColor Cyan

# WiX ships targeting an older net TFM than the SDK version this repo pins - LatestMajor makes sure it
# still resolves against whatever .NET is actually installed rather than depending on a second, older
# runtime also being present (same env var CI sets for the same reason).
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
wix build installer/windows/Package.wxs -ext WixToolset.UI.wixext `
    -d "InvokletVersion=$version" -d "PublishDir=$publishDir" `
    -arch x64 -out $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }

Write-Host "==> Built $msiPath" -ForegroundColor Green
