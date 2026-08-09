<#
.SYNOPSIS
    Build the NidusVMS recorder node as a self-contained, distributable package.

.DESCRIPTION
    Publishes NidusVMS.Node as a self-contained single-file executable for win-x64, then copies
    install-node.ps1 into the output folder so it can be zipped up and copied to a recorder
    machine as-is.

    Windows only for now: NodeConfigStore's registration store is DPAPI-based
    (System.Security.Cryptography.ProtectedData), which throws PlatformNotSupportedException on
    Linux. The plan's Linux node support (AES-256-GCM keyed off /etc/machine-id) isn't implemented
    yet, so there is no linux-x64 output here — publishing one would just fail at first run.

    Output:
        publish\NidusVMS.Node\win\   - NidusVMS.Node.exe + install-node.ps1

    There is no -Upload step (unlike dploid's build-agent.ps1): NidusVMS.NodeUpdater — the piece that
    would receive and apply an uploaded build — is still a stub. Until it exists, updating a node
    means re-running this script and install-node.ps1 on the recorder machine.

    -ExtraPublishPath optionally mirrors the same output to a second location (e.g. a network share
    a recorder machine can reach directly) so a node install/upgrade doesn't depend on manually
    copying the folder over each time.

.EXAMPLE
    .\build-node.ps1

.EXAMPLE
    .\build-node.ps1 -ExtraPublishPath '\\files1\nvr$\NidusVMS-node'
#>

param(
    [string]$NodeProject     = (Join-Path $PSScriptRoot 'src\NidusVMS.Node\NidusVMS.Node.csproj'),
    [string]$OutputRoot      = (Join-Path $PSScriptRoot 'publish\NidusVMS.Node'),
    [string]$Configuration   = 'Release',
    [string]$ExtraPublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$winOut = Join-Path $OutputRoot 'win'

Write-Step "Publishing NidusVMS.Node (win-x64, self-contained, single-file)"
if (Test-Path $winOut) { Remove-Item $winOut -Recurse -Force }
dotnet publish $NodeProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    -o $winOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Ok "Published"

Copy-Item (Join-Path $PSScriptRoot 'install-node.ps1') $winOut -Force
Write-Ok "install-node.ps1 bundled"

if ($ExtraPublishPath) {
    Write-Step "Mirroring package to $ExtraPublishPath"
    New-Item -ItemType Directory -Path $ExtraPublishPath -Force | Out-Null
    # /MIR so a stale file from a previous build (e.g. an old ffmpeg DLL that's no longer bundled)
    # doesn't linger alongside the new one; /R:2 /W:2 keeps a transient share hiccup from hanging
    # the whole build instead of failing fast.
    robocopy $winOut $ExtraPublishPath /MIR /R:2 /W:2 /NFL /NDL /NJH | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy to '$ExtraPublishPath' failed (exit $LASTEXITCODE)." }
    Write-Ok "Mirrored to $ExtraPublishPath"
}

Write-Host "`nPackage ready: $winOut" -ForegroundColor Yellow
if ($ExtraPublishPath) {
    Write-Host "Also mirrored to: $ExtraPublishPath" -ForegroundColor Yellow
    Write-Host "On the recorder machine (as Administrator), from that path:" -ForegroundColor DarkYellow
} else {
    Write-Host "Copy this folder to the recorder machine, then on that machine (as Administrator):" -ForegroundColor DarkYellow
}
Write-Host "  .\install-node.ps1 -ServerUrl <url> -RegistrationKey <key>" -ForegroundColor DarkYellow
Write-Host "`nDone." -ForegroundColor Green
