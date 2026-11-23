<#
.SYNOPSIS
    Build the LarisVMS media proxy as a self-contained, distributable package.

.DESCRIPTION
    Failover plan phase 2. Publishes LarisVMS.Proxy as a self-contained single-file executable for
    -Platform (win-x64 by default, or linux-x64), with LarisVMS.NodeUpdater alongside it. For win-x64
    it also copies install-proxy.ps1 in, so the folder can be zipped and copied to a relay machine
    as-is; a linux-x64 build is packaged as .deb and .rpm by build-linux-packages.ps1.

    Output (win-x64, unchanged; build-installers.ps1 and install-web.ps1 read it):
             publish\LarisVMS.Proxy\win\  - LarisVMS.Proxy.exe + LarisVMS.NodeUpdater.exe
                                            + install-proxy.ps1
             publish\LarisVMS.Proxy\      - proxy-build-version.txt, publisher.sig
    Output (linux-x64):
             publish\LarisVMS.Proxy\linux\           - LarisVMS.Proxy + LarisVMS.NodeUpdater
             publish\LarisVMS.Proxy\linux-x64-meta\  - proxy-build-version.txt, publisher.sig

    LarisVMS.NodeUpdater.exe is the same detached binary-swap helper the recorder node uses — it is
    already service-name-parameterised (`--service`), so the proxy reuses it verbatim (invoked with
    `--service LarisVMSProxy`) to swap its own binary during a self-triggered auto-update. See
    LarisVMS.Proxy/ProxyUpdateService.cs.

    Media proxies auto-update the same way recorder nodes do: install-web.ps1 -BuildProxy and
    build-installers.ps1 bundle the build into the Web package, and the Web server registers it as
    Pending (platform `proxy-<platform>`) when it starts; once an admin approves it, every proxy on
    that platform with an older version downloads, verifies, and applies it on its next check-in.
    Run this script standalone and nothing gets registered.

.PARAMETER PublisherKeyPath
    Optional: the publisher's encrypted private key (tools\publisher-key.ps1 -New). The build then
    carries the publisher's public key (src\LarisVMS.Core\Security\publisher-key.pem), is signed
    into publisher.sig, and installs only updates the publisher signed. Without it, the build is
    unsigned, as before. The password comes from LARISVMS_PUBLISHER_KEY_PASSWORD, or is asked for.

.EXAMPLE
    .\build-proxy.ps1

.EXAMPLE
    .\build-proxy.ps1 -ExtraPublishPath '\\files1\nvr$\LarisVMS-proxy'
#>

param(
    [string]$ProxyProject        = (Join-Path $PSScriptRoot 'src\LarisVMS.Proxy\LarisVMS.Proxy.csproj'),
    [string]$NodeUpdaterProject  = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj'),
    [string]$OutputRoot          = (Join-Path $PSScriptRoot 'publish\LarisVMS.Proxy'),
    [string]$Configuration       = 'Release',
    [string]$ExtraPublishPath,
    [string]$PublisherKeyPath,
    [ValidateSet('win-x64', 'linux-x64')][string]$Platform = 'win-x64',
    # Use this build number instead of taking the next one from build-number.txt (build-installers.ps1
    # builds every product and platform with one number).
    [int]$BuildNumber = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$isWindowsBuild = $Platform -eq 'win-x64'
$exe = if ($isWindowsBuild) { '.exe' } else { '' }
$pkgOut = Join-Path $OutputRoot $(if ($isWindowsBuild) { 'win' } else { 'linux' })
# Version and signature files; Windows keeps the layout build-installers.ps1 and install-web.ps1 read.
$metaRoot = if ($isWindowsBuild) { $OutputRoot } else { Join-Path $OutputRoot "$Platform-meta" }

# ── Publisher key (tools\publisher-key.ps1) ──────────────────────────────────
# Opt-in. Only a build signed with -PublisherKeyPath carries the publisher's public key, and then
# refuses any update the publisher didn't sign. Without it the build is unsigned and has no key, so
# building from source never needs the publisher's private key.
$publisherPublicKey = Join-Path $PSScriptRoot 'src\LarisVMS.Core\Security\publisher-key.pem'
if ($PublisherKeyPath -and -not (Test-Path $PublisherKeyPath)) { throw "Publisher key not found: $PublisherKeyPath" }
if ($PublisherKeyPath -and -not (Test-Path $publisherPublicKey)) {
    throw "No publisher public key at $publisherPublicKey. Make one with tools\publisher-key.ps1 -New first."
}
# Passed to every publish: embeds the public key in LarisVMS.Core (see its .csproj).
$publisherProps = if ($PublisherKeyPath) { @('-p:LarisVMSPublisherSigned=true') } else { @() }

function Write-PublisherSignature([string]$payload, [string]$path) {
    Write-Step "Signing with the publisher key"
    $sig = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'tools\publisher-key.ps1') -Sign -PrivateKeyPath $PublisherKeyPath -Payload $payload
    if ($LASTEXITCODE -ne 0 -or -not $sig) { throw 'Signing with the publisher key failed.' }
    Set-Content -Path $path -Value ($sig | Select-Object -Last 1) -NoNewline
    Write-Ok "Signed ($path)"
}

# ── Build number ─────────────────────────────────────────────────────────────
# Same shared, monotonic 4th version component as build-node.ps1 (0.192.0 -> 0.192.0.256), so a
# rebuild that doesn't change the hand-maintained semver still registers as a *newer* build and
# proxies auto-update to it. build-number.txt is the single source of truth; it is incremented here
# and committed with the release. NodeVersionComparer uses System.Version, which orders 4-part
# versions correctly. Plain `dotnet build` (dev/tests) never touches this — only the release build
# scripts do.
if ($BuildNumber -gt 0) {
    $buildNumber = $BuildNumber
} else {
    $buildNumberPath = Join-Path $PSScriptRoot 'build-number.txt'
    if (-not (Test-Path $buildNumberPath)) { throw "build-number.txt not found at $buildNumberPath." }
    $buildNumber = [int]((Get-Content $buildNumberPath -Raw).Trim()) + 1
    Set-Content -Path $buildNumberPath -Value $buildNumber -NoNewline
}

$versionMatch = Select-String -Path $ProxyProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) { throw "Could not find <Version> in '$ProxyProject'." }
$baseVersion = $versionMatch.Matches[0].Groups[1].Value.Trim()
$fullVersion = "$baseVersion.$buildNumber"
Write-Step "Build LarisVMS.Proxy $fullVersion (build number $buildNumber)"

New-Item -ItemType Directory -Path $OutputRoot, $metaRoot -Force | Out-Null
# Bundled beside the build (tools\BuildPackages.ps1), so the server registers exactly this version.
Set-Content -Path (Join-Path $metaRoot 'proxy-build-version.txt') -Value $fullVersion -NoNewline
$publisherSignaturePath = Join-Path $metaRoot 'publisher.sig'
Remove-Item $publisherSignaturePath -ErrorAction SilentlyContinue

Write-Step "Publishing LarisVMS.Proxy ($Platform, self-contained, single-file)"
if (Test-Path $pkgOut) { Remove-Item $pkgOut -Recurse -Force }
dotnet publish $ProxyProject `
    -c $Configuration `
    -r $Platform `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    "-p:Version=$fullVersion" `
    @publisherProps `
    -o $pkgOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Ok "Published"

Write-Step "Publishing LarisVMS.NodeUpdater ($Platform, self-contained, single-file)"
# Own temp folder, same reasoning as build-node.ps1's updater publish — a self-contained single-file
# publish drops its own copy of every shared runtime file, which would collide with the Proxy
# publish above if published straight into $pkgOut. Only the one binary this pass produces is copied.
$updaterTmp = Join-Path $OutputRoot "$Platform-updater-tmp"
if (Test-Path $updaterTmp) { Remove-Item $updaterTmp -Recurse -Force }
dotnet publish $NodeUpdaterProject `
    -c $Configuration `
    -r $Platform `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    "-p:Version=$fullVersion" `
    @publisherProps `
    -o $updaterTmp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Copy-Item (Join-Path $updaterTmp "LarisVMS.NodeUpdater$exe") $pkgOut -Force
Remove-Item $updaterTmp -Recurse -Force
Write-Ok "Published"

if ($PublisherKeyPath) {
    $proxyHash = (Get-FileHash (Join-Path $pkgOut "LarisVMS.Proxy$exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-PublisherSignature "larisvms-update:v1|proxy-$Platform|$fullVersion|$proxyHash|" $publisherSignaturePath
}

if (-not $isWindowsBuild) {
    # A Linux build is packaged by build-linux-packages.ps1 (called by build-installers.ps1).
    Write-Host "`nLinux build ready: $pkgOut (package it with build-linux-packages.ps1)" -ForegroundColor Yellow
    Write-Host "`nDone." -ForegroundColor Green
    return
}

Copy-Item (Join-Path $PSScriptRoot 'install-proxy.ps1') $pkgOut -Force
Write-Ok "install-proxy.ps1 bundled"

if ($ExtraPublishPath) {
    Write-Step "Mirroring package to $ExtraPublishPath"
    New-Item -ItemType Directory -Path $ExtraPublishPath -Force | Out-Null
    robocopy $pkgOut $ExtraPublishPath /MIR /R:2 /W:2 /NFL /NDL /NJH | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy to '$ExtraPublishPath' failed (exit $LASTEXITCODE)." }
    Write-Ok "Mirrored to $ExtraPublishPath"
}

Write-Host "`nPackage ready: $pkgOut" -ForegroundColor Yellow
Write-Host "On the relay machine (as Administrator), from that folder:" -ForegroundColor DarkYellow
Write-Host "  .\install-proxy.ps1 -ServerUrl <url> -RegistrationKey <key> -ClientPort 4443" -ForegroundColor DarkYellow
Write-Host "`nDone." -ForegroundColor Green
