<#
.SYNOPSIS
    Build the LarisVMS recorder node as a self-contained, distributable package.

.DESCRIPTION
    Publishes LarisVMS.Node and LarisVMS.NodeUpdater as self-contained single-file executables for
    win-x64, then copies install-node.ps1 into the output folder so it can be zipped up and copied to
    a recorder machine as-is.

    Windows only for now: NodeConfigStore's registration store is DPAPI-based
    (System.Security.Cryptography.ProtectedData), which throws PlatformNotSupportedException on
    Linux. The plan's Linux node support (AES-256-GCM keyed off /etc/machine-id) isn't implemented
    yet, so there is no linux-x64 output here — publishing one would just fail at first run.

    Output:
        publish\LarisVMS.Node\win\   - LarisVMS.Node.exe + LarisVMS.NodeUpdater.exe + install-node.ps1
                                       + (unless -SkipVision) LarisVMS.Vision.Service.exe + models\

    LarisVMS.NodeUpdater.exe is what a node launches (as a detached process) to swap its own binary
    during a self-triggered auto-update — see LarisVMS.Node/Update/UpdateService.cs and
    LarisVMS.NodeUpdater/Program.cs. It's built and bundled here so it's always present alongside
    LarisVMS.Node.exe. There is still no -Upload step in this script itself (unlike dploid's
    build-agent.ps1) — registering the built LarisVMS.Node.exe with LarisVMS.Web's node-build-approval
    queue (Admin -> Node Builds) is deploy.ps1's job, which calls this script and then registers
    whatever it just built directly against the server's own database/storage. Run this script
    standalone (as build-node.ps1 -ExtraPublishPath ...) and nothing gets registered — only
    deploy.ps1's own run does that.

    -Accel picks which single execution-provider variant of LarisVMS.Vision.Service gets built into
    this package (object detection plan decisions 2/3) — Cuda (Nvidia), DirectML (any DX12 GPU incl.
    AMD/Intel), OpenVino (Intel iGPU/CPU), or Cpu (no GPU; the default, since it's the one variant
    every machine can actually run). Exactly one variant is published per node package, always as the
    same fixed filename LarisVMS.Vision.Service.exe (see that project's own csproj comment) — the
    admin's Node.AiAccelerator setting on the server picks which physical hardware NodeWorker actually
    tries to use, but that choice has to match whichever provider this specific machine's package was
    actually built with; there's no way to switch providers at runtime (YoloDotNet only links one
    execution-provider package per process). Sites with genuinely different hardware need separate
    packages built with different -Accel values. -SkipVision omits it entirely, for a plain
    recording-only node with no AI detection at all.

    Vision Service needs a fetched .onnx model to do anything — this script copies models\*.onnx
    from the repo root (gitignored, produced by tools/export-models/fetch_dfine.py) into the package
    if present, and just warns (doesn't fail the build) if it's missing, the same "detection is
    additive, never a hard dependency" philosophy NodeWorker itself already applies when no
    accelerator is available.

    -ExtraPublishPath optionally mirrors the same output to a second location (e.g. a network share
    a recorder machine can reach directly) so a node install/upgrade doesn't depend on manually
    copying the folder over each time.

.EXAMPLE
    .\build-node.ps1

.EXAMPLE
    .\build-node.ps1 -Accel Cuda -ExtraPublishPath '\\files1\nvr$\LarisVMS-node'

.EXAMPLE
    .\build-node.ps1 -SkipVision
#>

param(
    [string]$NodeProject        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj'),
    [string]$NodeUpdaterProject = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj'),
    [string]$VisionProject      = (Join-Path $PSScriptRoot 'src\LarisVMS.Vision.Service\LarisVMS.Vision.Service.csproj'),
    [string]$ModelsPath         = (Join-Path $PSScriptRoot 'models'),
    [string]$OutputRoot         = (Join-Path $PSScriptRoot 'publish\LarisVMS.Node'),
    [string]$Configuration      = 'Release',
    [ValidateSet('Cuda', 'DirectML', 'OpenVino', 'Cpu')]
    [string]$Accel              = 'Cpu',
    [switch]$SkipVision,
    [string]$ExtraPublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$winOut = Join-Path $OutputRoot 'win'

Write-Step "Publishing LarisVMS.Node (win-x64, self-contained, single-file)"
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

Write-Step "Publishing LarisVMS.NodeUpdater (win-x64, self-contained, single-file)"
# Published to its own temp folder, not straight into $winOut — a single-file self-contained publish
# drops its own copy of every shared runtime file (hostfxr, etc.) into the output directory, and
# publishing two different projects into the same folder back-to-back would have this pass's files
# collide with (and potentially get partially overwritten by) the Node publish above. Only the one
# binary this pass actually produces gets copied over.
$updaterTmp = Join-Path $OutputRoot 'win-updater-tmp'
if (Test-Path $updaterTmp) { Remove-Item $updaterTmp -Recurse -Force }
dotnet publish $NodeUpdaterProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:NoWarn=CA1416 `
    -o $updaterTmp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Copy-Item (Join-Path $updaterTmp 'LarisVMS.NodeUpdater.exe') $winOut -Force
Remove-Item $updaterTmp -Recurse -Force
Write-Ok "Published"

if (-not $SkipVision) {
    Write-Step "Publishing LarisVMS.Vision.Service (win-x64, self-contained, single-file, Accel=$Accel)"
    # Own temp folder, same reasoning as the NodeUpdater publish above — a self-contained single-file
    # publish drops its own copy of shared runtime files, which would collide with Node's if published
    # straight into $winOut.
    $visionTmp = Join-Path $OutputRoot 'win-vision-tmp'
    if (Test-Path $visionTmp) { Remove-Item $visionTmp -Recurse -Force }
    dotnet publish $VisionProject `
        -c $Configuration `
        -r win-x64 `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:NoWarn=CA1416 `
        -p:Accel=$Accel `
        -o $visionTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    # Everything from this publish, not just the .exe — the chosen execution provider's own native
    # DLLs (onnxruntime, cuDNN/TensorRT for Cuda, etc.) sit alongside it, not inside the single file.
    Copy-Item (Join-Path $visionTmp '*') $winOut -Recurse -Force
    Remove-Item $visionTmp -Recurse -Force
    Write-Ok "Published (Accel=$Accel)"

    Write-Step "Bundling exported model(s)"
    $modelsOut = Join-Path $winOut 'models'
    New-Item -ItemType Directory -Path $modelsOut -Force | Out-Null
    # -Recurse kept even though fetch_dfine.py now writes flat into models\ directly (a stale/
    # hand-placed model nested in a subfolder should still be found rather than silently ignored,
    # and this cost nothing when the older exporter did nest its own output under models\weights\).
    # Flattened into the package's own models\ folder below regardless of how deep it was found.
    $onnxFiles = @(if (Test-Path $ModelsPath) { Get-ChildItem $ModelsPath -Filter '*.onnx' -File -Recurse } else { @() })
    if ($onnxFiles.Count -eq 0) {
        Write-Host "    No .onnx model found under $ModelsPath — AI detection won't have anything to run until one is placed there (see tools/export-models/) and this is rebuilt." -ForegroundColor Yellow
    } else {
        $onnxFiles | Copy-Item -Destination $modelsOut -Force
        Write-Ok "Bundled $($onnxFiles.Count) model file(s)"
    }
} else {
    Write-Step "Skipping LarisVMS.Vision.Service (-SkipVision) — this package will be recording-only, no AI detection."
}

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
