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
                                       + (unless -SkipVision) LarisVMS.Vision.Service.exe
                                         + onnxruntime-backends\{cuda,directml}\
                                       (no models\ — see below; never bundled)

    LarisVMS.NodeUpdater.exe is what a node launches (as a detached process) to swap its own binary
    during a self-triggered auto-update — see LarisVMS.Node/Update/UpdateService.cs and
    LarisVMS.NodeUpdater/Program.cs. It's built and bundled here so it's always present alongside
    LarisVMS.Node.exe. There is still no -Upload step in this script itself (unlike dploid's
    build-agent.ps1) — registering the built LarisVMS.Node.exe with LarisVMS.Web's node-build-approval
    queue (Admin -> Node Builds) is deploy.ps1's job, which calls this script and then registers
    whatever it just built directly against the server's own database/storage. Run this script
    standalone (as build-node.ps1 -ExtraPublishPath ...) and nothing gets registered — only
    deploy.ps1's own run does that.

    There is no -Accel switch any more. LarisVMS.Vision.Service.csproj's StageOnnxBackends target
    stages the native ONNX Runtime GPU builds — CUDA(+TensorRT) and DirectML — into
    onnxruntime-backends\<name>\ beside LarisVMS.Vision.Service.exe (CPU is the plain onnxruntime.dll
    at the root), so one node package runs on any hardware. At startup the Vision
    Service picks the backend that matches the accelerator the node resolved for its machine
    (VisionBackendResolver), degrading CUDA -> DirectML -> CPU when a toolkit is missing and
    reporting that back so Admin/Nodes can flag it. A machine that wants CUDA still needs the CUDA
    Toolkit 12.x + cuDNN 9.x installed (install-node.ps1 detects this and prints what to install);
    DirectML/OpenVINO/CPU need nothing extra. -SkipVision omits the Vision Service entirely, for a
    plain recording-only node with no AI detection at all.

    Vision Service needs at least one .onnx model to do anything, but models are never bundled into
    this package — they live in C:\ProgramData\LarisVMS\models on the node itself (an operator- or
    install-managed location, not build output), scanned at runtime by
    LarisVMS.Vision.Models.ModelDiscovery. This script only prints a reminder of that; it neither
    copies nor requires a model to be present, the same "detection is additive, never a hard
    dependency" philosophy NodeWorker itself already applies when no accelerator is available.

    -ExtraPublishPath optionally mirrors the same output to a second location (e.g. a network share
    a recorder machine can reach directly) so a node install/upgrade doesn't depend on manually
    copying the folder over each time.

.EXAMPLE
    .\build-node.ps1

.EXAMPLE
    .\build-node.ps1 -ExtraPublishPath '\\files1\nvr$\LarisVMS-node'

.EXAMPLE
    .\build-node.ps1 -SkipVision
#>

param(
    [string]$NodeProject        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj'),
    [string]$NodeUpdaterProject = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj'),
    [string]$VisionProject      = (Join-Path $PSScriptRoot 'src\LarisVMS.Vision.Service\LarisVMS.Vision.Service.csproj'),
    [string]$OutputRoot         = (Join-Path $PSScriptRoot 'publish\LarisVMS.Node'),
    [string]$Configuration      = 'Release',
    [switch]$SkipVision,
    [string]$ExtraPublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$winOut = Join-Path $OutputRoot 'win'

# ── Build number ─────────────────────────────────────────────────────────────
# Every node package this script produces gets a monotonically increasing 4th version component
# (0.188.0 -> 0.188.0.244), so a rebuild that doesn't change the hand-maintained semver still
# registers as a *newer* NodeBuildVersion and recorder nodes auto-update to it for testing.
# build-number.txt is the single source of truth (a file, not a DB row); it is incremented here and
# committed with the release. NodeVersionComparer uses System.Version, which orders 4-part versions
# correctly, so nothing on the compare side changes. Plain `dotnet build` (dev/tests) never touches
# this — only the release build scripts do.
$buildNumberPath = Join-Path $PSScriptRoot 'build-number.txt'
if (-not (Test-Path $buildNumberPath)) { throw "build-number.txt not found at $buildNumberPath." }
$buildNumber = [int]((Get-Content $buildNumberPath -Raw).Trim()) + 1
Set-Content -Path $buildNumberPath -Value $buildNumber -NoNewline

$baseVersionMatch = Select-String -Path $NodeProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $baseVersionMatch) { throw "Could not find <Version> in '$NodeProject'." }
$baseVersion = $baseVersionMatch.Matches[0].Groups[1].Value.Trim()
$fullVersion = "$baseVersion.$buildNumber"
Write-Step "Build $fullVersion (build number $buildNumber)"

# Read back by deploy.ps1's node-build registration so it registers exactly this version.
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Set-Content -Path (Join-Path $OutputRoot 'node-build-version.txt') -Value $fullVersion -NoNewline

Write-Step "Publishing LarisVMS.Node (win-x64, self-contained, single-file)"
if (Test-Path $winOut) { Remove-Item $winOut -Recurse -Force }
dotnet publish $NodeProject `
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
    "-p:Version=$fullVersion" `
    -o $updaterTmp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Copy-Item (Join-Path $updaterTmp 'LarisVMS.NodeUpdater.exe') $winOut -Force
Remove-Item $updaterTmp -Recurse -Force
Write-Ok "Published"

if (-not $SkipVision) {
    Write-Step "Publishing LarisVMS.Vision.Service (win-x64, self-contained, single-file, all ONNX Runtime backends)"
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
        "-p:Version=$fullVersion" `
        -o $visionTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    # Everything from this publish, not just the .exe — the managed ONNX Runtime assembly plus the
    # onnxruntime-backends\{cuda,directml}\ tree LarisVMS.Vision.Service.csproj's StageOnnxBackends
    # target lays down (each backend's own onnxruntime.dll + provider natives), all beside the exe
    # rather than inside the single file.
    Copy-Item (Join-Path $visionTmp '*') $winOut -Recurse -Force
    Remove-Item $visionTmp -Recurse -Force
    if (-not (Test-Path (Join-Path $winOut 'onnxruntime-backends'))) {
        throw "Vision publish produced no onnxruntime-backends\ folder — StageOnnxBackends did not run. AI detection would not work on any node."
    }
    Write-Ok "Published (ONNX Runtime backends bundled)"

    # onnxruntime_providers_cuda.dll is ~320 MB and only NVIDIA nodes ever load it — pull it OUT of
    # the package into a sibling folder deploy.ps1 seeds into the server's vision-native cache, so a
    # node that resolves CUDA downloads it once instead of it bloating every CPU/DirectML install.
    # See CudaProviderProvisioner. The small CUDA files (onnxruntime.dll, providers_shared/tensorrt)
    # stay in the package so the resolver can still see a cuda\ folder.
    $cudaProviderDll = Join-Path $winOut 'onnxruntime-backends\cuda\onnxruntime_providers_cuda.dll'
    if (Test-Path $cudaProviderDll) {
        $cudaProviderOut = Join-Path $OutputRoot 'cuda-provider'
        if (Test-Path $cudaProviderOut) { Remove-Item $cudaProviderOut -Recurse -Force }
        New-Item -ItemType Directory -Path $cudaProviderOut -Force | Out-Null
        Move-Item $cudaProviderDll (Join-Path $cudaProviderOut 'onnxruntime_providers_cuda.dll') -Force
        $sizeMb = [math]::Round((Get-Item (Join-Path $cudaProviderOut 'onnxruntime_providers_cuda.dll')).Length / 1MB)
        Write-Ok "CUDA provider ($sizeMb MB) split out to $cudaProviderOut (not shipped in the package — deploy.ps1 seeds it to the server)"
    } else {
        Write-Host "    onnxruntime_providers_cuda.dll not found in the CUDA backend — CUDA acceleration won't be available on any node." -ForegroundColor Yellow
    }

    Write-Host "    Models are not bundled into the package — drop .onnx files (with an optional same-basename .json sidecar) into C:\ProgramData\LarisVMS\models on the node itself. See tools/export-models/ for how to obtain one." -ForegroundColor Yellow
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
