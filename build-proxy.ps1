<#
.SYNOPSIS
    Build the LarisVMS media proxy as a self-contained, distributable package.

.DESCRIPTION
    Failover plan phase 2. Publishes LarisVMS.Proxy as a self-contained single-file executable for
    win-x64, then copies install-proxy.ps1 and LarisVMS.NodeUpdater.exe alongside it so the folder
    can be zipped and copied to a relay machine as-is.

    Output:  publish\LarisVMS.Proxy\win\  - LarisVMS.Proxy.exe + LarisVMS.NodeUpdater.exe
                                            + install-proxy.ps1

    LarisVMS.NodeUpdater.exe is the same detached binary-swap helper the recorder node uses — it is
    already service-name-parameterised (`--service`), so the proxy reuses it verbatim (invoked with
    `--service LarisVMSProxy`) to swap its own binary during a self-triggered auto-update. See
    LarisVMS.Proxy/ProxyUpdateService.cs.

    Media proxies auto-update the same way recorder nodes do: `deploy.ps1 -BuildProxy` registers the
    built exe with LarisVMS.Web's build-approval queue (Admin -> Node Builds, as platform
    `proxy-win-x64`) as Pending; once an admin approves it, every proxy whose reported version is
    older downloads, verifies and applies it on its own next check-in. Run this script standalone and
    nothing gets registered — only deploy.ps1's own run does that.

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
    [string]$ExtraPublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$winOut = Join-Path $OutputRoot 'win'

# ── Build number ─────────────────────────────────────────────────────────────
# Same shared, monotonic 4th version component as build-node.ps1 (0.192.0 -> 0.192.0.256), so a
# rebuild that doesn't change the hand-maintained semver still registers as a *newer* build and
# proxies auto-update to it. build-number.txt is the single source of truth; it is incremented here
# and committed with the release. NodeVersionComparer uses System.Version, which orders 4-part
# versions correctly. Plain `dotnet build` (dev/tests) never touches this — only the release build
# scripts do.
$buildNumberPath = Join-Path $PSScriptRoot 'build-number.txt'
if (-not (Test-Path $buildNumberPath)) { throw "build-number.txt not found at $buildNumberPath." }
$buildNumber = [int]((Get-Content $buildNumberPath -Raw).Trim()) + 1
Set-Content -Path $buildNumberPath -Value $buildNumber -NoNewline

$versionMatch = Select-String -Path $ProxyProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) { throw "Could not find <Version> in '$ProxyProject'." }
$baseVersion = $versionMatch.Matches[0].Groups[1].Value.Trim()
$fullVersion = "$baseVersion.$buildNumber"
Write-Step "Build LarisVMS.Proxy $fullVersion (build number $buildNumber)"

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
# Read back by deploy.ps1's proxy-build registration so it registers exactly this version.
Set-Content -Path (Join-Path $OutputRoot 'proxy-build-version.txt') -Value $fullVersion -NoNewline

Write-Step "Publishing LarisVMS.Proxy (win-x64, self-contained, single-file)"
if (Test-Path $winOut) { Remove-Item $winOut -Recurse -Force }
dotnet publish $ProxyProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    "-p:Version=$fullVersion" `
    -o $winOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Ok "Published"

Write-Step "Publishing LarisVMS.NodeUpdater (win-x64, self-contained, single-file)"
# Own temp folder, same reasoning as build-node.ps1's updater publish — a self-contained single-file
# publish drops its own copy of every shared runtime file, which would collide with the Proxy
# publish above if published straight into $winOut. Only the one binary this pass produces is copied.
$updaterTmp = Join-Path $OutputRoot 'win-updater-tmp'
if (Test-Path $updaterTmp) { Remove-Item $updaterTmp -Recurse -Force }
dotnet publish $NodeUpdaterProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    "-p:Version=$fullVersion" `
    -o $updaterTmp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Copy-Item (Join-Path $updaterTmp 'LarisVMS.NodeUpdater.exe') $winOut -Force
Remove-Item $updaterTmp -Recurse -Force
Write-Ok "Published"

Copy-Item (Join-Path $PSScriptRoot 'install-proxy.ps1') $winOut -Force
Write-Ok "install-proxy.ps1 bundled"

if ($ExtraPublishPath) {
    Write-Step "Mirroring package to $ExtraPublishPath"
    New-Item -ItemType Directory -Path $ExtraPublishPath -Force | Out-Null
    robocopy $winOut $ExtraPublishPath /MIR /R:2 /W:2 /NFL /NDL /NJH | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy to '$ExtraPublishPath' failed (exit $LASTEXITCODE)." }
    Write-Ok "Mirrored to $ExtraPublishPath"
}

Write-Host "`nPackage ready: $winOut" -ForegroundColor Yellow
Write-Host "On the relay machine (as Administrator), from that folder:" -ForegroundColor DarkYellow
Write-Host "  .\install-proxy.ps1 -ServerUrl <url> -RegistrationKey <key> -ClientPort 4443" -ForegroundColor DarkYellow
Write-Host "`nDone." -ForegroundColor Green
