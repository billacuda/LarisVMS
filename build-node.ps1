<#
.SYNOPSIS
    Build the Rcordr recorder node as a self-contained, distributable package.

.DESCRIPTION
    Publishes Rcordr.Node as a self-contained single-file executable for win-x64, then copies
    install-node.ps1 into the output folder so it can be zipped up and copied to a recorder
    machine as-is.

    Windows only for now: NodeConfigStore's registration store is DPAPI-based
    (System.Security.Cryptography.ProtectedData), which throws PlatformNotSupportedException on
    Linux. The plan's Linux node support (AES-256-GCM keyed off /etc/machine-id) isn't implemented
    yet, so there is no linux-x64 output here — publishing one would just fail at first run.

    Output:
        publish\Rcordr.Node\win\   - Rcordr.Node.exe + install-node.ps1

    There is no -Upload step (unlike dploid's build-agent.ps1): Rcordr.NodeUpdater — the piece that
    would receive and apply an uploaded build — is still a stub. Until it exists, updating a node
    means re-running this script and install-node.ps1 on the recorder machine.

.EXAMPLE
    .\build-node.ps1
#>

param(
    [string]$NodeProject   = (Join-Path $PSScriptRoot 'src\Rcordr.Node\Rcordr.Node.csproj'),
    [string]$OutputRoot    = (Join-Path $PSScriptRoot 'publish\Rcordr.Node'),
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$winOut = Join-Path $OutputRoot 'win'

Write-Step "Publishing Rcordr.Node (win-x64, self-contained, single-file)"
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

Write-Host "`nPackage ready: $winOut" -ForegroundColor Yellow
Write-Host "Copy this folder to the recorder machine, then on that machine (as Administrator):" -ForegroundColor DarkYellow
Write-Host "  .\install-node.ps1 -ServerUrl <url> -RegistrationKey <key>" -ForegroundColor DarkYellow
Write-Host "`nDone." -ForegroundColor Green
