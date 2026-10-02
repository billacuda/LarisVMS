<#
.SYNOPSIS
    Builds the LarisVMS Web, Node and Proxy MSI installers.

.DESCRIPTION
      1. Checks the release version is in sync (CHANGELOG.md, every csproj <Version>, and the
         BumpVersionX_Y_Z migration) — see tools\VersionGuard.ps1
      2. Builds the recorder node and media proxy packages (build-node.ps1, build-proxy.ps1)
      3. Publishes LarisVMS.Web self-contained (win-x64) and bundles the node/proxy packages and the
         CUDA provider into its packages\ folder — the web app registers them as Pending on startup
      4. Builds the three MSIs with WiX v5 (restored from NuGet; no global install needed)
      5. Copies them to publish\installers\LarisVMS-{Web,Node,Proxy}-<version>-x64.msi, optionally
         signing them

    Needs the .NET 10 SDK. Does not need administrator rights — nothing is installed.

.EXAMPLE
    .\build-installers.ps1
    .\build-installers.ps1 -SkipNodeVision
    .\build-installers.ps1 -SignThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir     = (Join-Path $PSScriptRoot 'publish\installers'),
    [switch]$SkipNodeVision,
    [switch]$SkipVersionSyncCheck,
    # Optional code-signing certificate (CurrentUser\My or LocalMachine\My) used for the MSIs.
    [string]$SignThumbprint = '',
    [string]$TimestampUrl   = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host $Message -ForegroundColor Green }
function Invoke-Cmd([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

$webProject = Join-Path $PSScriptRoot 'src\LarisVMS.Web\LarisVMS.Web.csproj'
$publishWeb = Join-Path $PSScriptRoot 'publish\LarisVMS.Web'

# ── version ──────────────────────────────────────────────────────────────────
. (Join-Path $PSScriptRoot 'tools\VersionGuard.ps1')
$changelog = Join-Path $PSScriptRoot 'CHANGELOG.md'
if ($SkipVersionSyncCheck) {
    $version = Get-CsprojVersion $webProject
    Write-Host "Skipping version sync guard - using LarisVMS.Web's <Version> $version."
} else {
    Write-Step "Checking release version"
    $version = Assert-ReleaseVersionSync -ChangelogPath $changelog `
        -MigrationsPath (Join-Path $PSScriptRoot 'src\LarisVMS.Infrastructure\Migrations') -Projects @{
            'LarisVMS.Web'         = $webProject
            'LarisVMS.Node'        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj')
            'LarisVMS.Proxy'       = (Join-Path $PSScriptRoot 'src\LarisVMS.Proxy\LarisVMS.Proxy.csproj')
            'LarisVMS.NodeUpdater' = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj')
        }
    Write-Ok "Release $version"
}

# ── node and proxy packages ──────────────────────────────────────────────────
Write-Step "Building recorder node package"
$nodeArgs = @{ Configuration = $Configuration }
if ($SkipNodeVision) { $nodeArgs['SkipVision'] = $true }
& (Join-Path $PSScriptRoot 'build-node.ps1') @nodeArgs

Write-Step "Building media proxy package"
& (Join-Path $PSScriptRoot 'build-proxy.ps1') -Configuration $Configuration

# ── web, self-contained, with the packages bundled ───────────────────────────
Write-Step "Publishing LarisVMS.Web (self-contained win-x64)"
if (Test-Path $publishWeb) { Remove-Item $publishWeb -Recurse -Force }
Invoke-Cmd 'dotnet' @('publish', $webProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', '-o', $publishWeb)

$packagesDir = Join-Path $publishWeb 'packages'
$bundles = @(
    @{ Src = 'publish\LarisVMS.Node\win\LarisVMS.Node.exe';                       Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Node\win\LarisVMS.Vision.Service.exe';             Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Node\node-build-version.txt';                      Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Proxy\win\LarisVMS.Proxy.exe';                     Dest = 'proxy' },
    @{ Src = 'publish\LarisVMS.Proxy\proxy-build-version.txt';                    Dest = 'proxy' },
    @{ Src = 'publish\LarisVMS.Node\cuda-provider\onnxruntime_providers_cuda.dll'; Dest = 'cuda-provider' }
)
foreach ($b in $bundles) {
    $src = Join-Path $PSScriptRoot $b.Src
    if (-not (Test-Path $src)) { Write-Host "    (not built, skipped) $($b.Src)" -ForegroundColor DarkGray; continue }
    $dest = Join-Path $packagesDir $b.Dest
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item $src $dest -Force
}
Write-Ok "Web published with node/proxy packages bundled."

# ── MSIs ─────────────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$built = @()
foreach ($product in @('Web', 'Node', 'Proxy')) {
    Write-Step "Building LarisVMS-$product MSI"
    $proj = Join-Path $PSScriptRoot "installer\$product\LarisVMS.$product.Installer.wixproj"
    Invoke-Cmd 'dotnet' @('build', $proj, '-c', $Configuration, "-p:ProductVersion=$version", '-nologo', '-v', 'minimal')
    $msi = Join-Path $PSScriptRoot "installer\$product\bin\$Configuration\LarisVMS-$product.msi"
    $target = Join-Path $OutputDir "LarisVMS-$product-$version-x64.msi"
    Copy-Item $msi $target -Force
    $built += $target
}

if ($SignThumbprint) {
    Write-Step "Signing"
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe not found - install the Windows SDK, or omit -SignThumbprint." }
    foreach ($msi in $built) {
        Invoke-Cmd $signtool @('sign', '/sha1', $SignThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $msi)
    }
}

Write-Host ""
Write-Ok "Installers:"
$built | ForEach-Object { Write-Host "  $_  ($([math]::Round((Get-Item $_).Length / 1MB)) MB)" }
