<#
.SYNOPSIS
    Build the LarisVMS recorder node as a self-contained, distributable package.

.DESCRIPTION
    Publishes LarisVMS.Node and LarisVMS.NodeUpdater as self-contained single-file executables for
    -Platform (win-x64 by default, or linux-x64). For win-x64 it copies install-node.ps1 into the
    output folder so it can be zipped up and copied to a recorder machine as-is; a linux-x64 build is
    packaged as .deb and .rpm by build-linux-packages.ps1.

    Output (win-x64, unchanged; build-installers.ps1 and install-web.ps1 read it):
        publish\LarisVMS.Node\win\   - LarisVMS.Node.exe + LarisVMS.NodeUpdater.exe + install-node.ps1
                                       + (unless -SkipVision) LarisVMS.Vision.Service.exe
                                         + onnxruntime-backends\{cuda,directml,openvino}\
                                       (no models\ — see below; never bundled)
        publish\LarisVMS.Node\       - node-build-version.txt, publisher.sig, backends\, cuda-provider\
    Output (linux-x64):
        publish\LarisVMS.Node\linux\           - LarisVMS.Node + LarisVMS.NodeUpdater
                                                 + LarisVMS.Vision.Service + onnxruntime-backends/{cuda,openvino}/
        publish\LarisVMS.Node\linux-x64-meta\  - node-build-version.txt, publisher.sig, backends\, cuda-provider\

    LarisVMS.NodeUpdater.exe is what a node launches (as a detached process) to swap its own binary
    during a self-triggered auto-update — see LarisVMS.Node/Update/UpdateService.cs and
    LarisVMS.NodeUpdater/Program.cs. It's built and bundled here so it's always present alongside
    LarisVMS.Node.exe. This script registers nothing with the server: install-web.ps1 and
    build-installers.ps1 bundle its output into the Web package's packages\ folder, and the Web
    server registers those builds as Pending when it starts.

    There is no -Accel switch any more. LarisVMS.Vision.Service.csproj's StageOnnxBackends target
    stages the native ONNX Runtime GPU builds — CUDA(+TensorRT) and DirectML — and Intel's OpenVINO
    plugin into onnxruntime-backends\<name>\ beside LarisVMS.Vision.Service.exe (CPU is the plain
    onnxruntime.dll at the root; the OpenVINO plugin loads on top of it), so one node package runs on
    any hardware. At startup the Vision
    Service picks the backend that matches the accelerator the node resolved for its machine
    (VisionBackendResolver), degrading CUDA -> DirectML -> CPU (Intel: OpenVINO -> DirectML -> CPU)
    when a toolkit is missing and
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

.PARAMETER PublisherKeyPath
    Optional: the publisher's encrypted private key (tools\publisher-key.ps1 -New). The build then
    carries the publisher's public key (src\LarisVMS.Core\Security\publisher-key.pem), is signed
    into publisher.sig, and installs only updates the publisher signed. Without it, the build is
    unsigned, as before. The password comes from LARISVMS_PUBLISHER_KEY_PASSWORD, or is asked for.

.EXAMPLE
    .\build-node.ps1

.EXAMPLE
    .\build-node.ps1 -ExtraPublishPath '\\files1\nvr$\LarisVMS-node'

.EXAMPLE
    .\build-node.ps1 -SkipVision

.EXAMPLE
    .\build-node.ps1 -Platform linux-x64 -BuildNumber 700
#>

param(
    [string]$NodeProject        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj'),
    [string]$NodeUpdaterProject = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj'),
    [string]$VisionProject      = (Join-Path $PSScriptRoot 'src\LarisVMS.Vision.Service\LarisVMS.Vision.Service.csproj'),
    [string]$OutputRoot         = (Join-Path $PSScriptRoot 'publish\LarisVMS.Node'),
    [string]$Configuration      = 'Release',
    [switch]$SkipVision,
    [string]$ExtraPublishPath,
    [string]$PublisherKeyPath,
    [ValidateSet('win-x64', 'linux-x64')][string]$Platform = 'win-x64',
    # Use this build number instead of taking the next one from build-number.txt: build-installers.ps1
    # takes one number per run and builds every platform with it, so they all share one version.
    [int]$BuildNumber = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

$isWindowsBuild = $Platform -eq 'win-x64'
$exe = if ($isWindowsBuild) { '.exe' } else { '' }
# The package folder, and the files that go with it to the server (version, signatures, the backends
# bundle, and the CUDA provider). Windows keeps the layout build-installers.ps1 and install-web.ps1 read.
$pkgOut = Join-Path $OutputRoot $(if ($isWindowsBuild) { 'win' } else { 'linux' })
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

# Exactly LarisVMS.Core.Update.BackendsBundle.ComputeStamp: SHA-256 over one "path<TAB>sha256<LF>" line
# per file ('/' separators, ordinal order, .stamp itself left out), lowercase hex. Nodes check the
# unpacked tree against it, so the two must never drift apart.
function Get-BackendsStamp([string]$dir) {
    # One DirectoryInfo for the root and its files, so both spell the path the same way (Resolve-Path
    # can return an 8.3 short name while Get-ChildItem's FullName is the long one).
    $rootInfo = New-Object System.IO.DirectoryInfo ((Resolve-Path $dir).Path)
    $root = $rootInfo.FullName.TrimEnd('\', '/')
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in $rootInfo.EnumerateFiles('*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -eq '.stamp') { continue }
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("$relative`t$hash`n")
    }
    $lines.Sort([System.StringComparer]::Ordinal)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join ''))) } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($digest) -replace '-', '').ToLowerInvariant()
}

# ── Build number ─────────────────────────────────────────────────────────────
# Every node package this script produces gets a monotonically increasing 4th version component
# (0.188.0 -> 0.188.0.244), so a rebuild that doesn't change the hand-maintained semver still
# registers as a *newer* NodeBuildVersion and recorder nodes auto-update to it for testing.
# build-number.txt is the single source of truth (a file, not a DB row); it is incremented here and
# committed with the release. NodeVersionComparer uses System.Version, which orders 4-part versions
# correctly, so nothing on the compare side changes. Plain `dotnet build` (dev/tests) never touches
# this — only the release build scripts do.
if ($BuildNumber -gt 0) {
    $buildNumber = $BuildNumber
} else {
    $buildNumberPath = Join-Path $PSScriptRoot 'build-number.txt'
    if (-not (Test-Path $buildNumberPath)) { throw "build-number.txt not found at $buildNumberPath." }
    $buildNumber = [int]((Get-Content $buildNumberPath -Raw).Trim()) + 1
    Set-Content -Path $buildNumberPath -Value $buildNumber -NoNewline
}

$baseVersionMatch = Select-String -Path $NodeProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $baseVersionMatch) { throw "Could not find <Version> in '$NodeProject'." }
$baseVersion = $baseVersionMatch.Matches[0].Groups[1].Value.Trim()
$fullVersion = "$baseVersion.$buildNumber"
Write-Step "Build $fullVersion (build number $buildNumber)"

# Bundled beside the build (tools\BuildPackages.ps1), so the server registers exactly this version.
New-Item -ItemType Directory -Path $OutputRoot, $metaRoot -Force | Out-Null
Set-Content -Path (Join-Path $metaRoot 'node-build-version.txt') -Value $fullVersion -NoNewline
# A signature left from an earlier build would be registered with this one.
$publisherSignaturePath = Join-Path $metaRoot 'publisher.sig'
Remove-Item $publisherSignaturePath -ErrorAction SilentlyContinue
# Likewise the backends bundle and CUDA provider: a -SkipVision build must not register an earlier build's.
foreach ($stale in @('backends', 'cuda-provider')) {
    $stalePath = Join-Path $metaRoot $stale
    if (Test-Path $stalePath) { Remove-Item $stalePath -Recurse -Force }
}

Write-Step "Publishing LarisVMS.Node ($Platform, self-contained, single-file)"
if (Test-Path $pkgOut) { Remove-Item $pkgOut -Recurse -Force }
dotnet publish $NodeProject `
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
# Published to its own temp folder, not straight into $pkgOut — a single-file self-contained publish
# drops its own copy of every shared runtime file (hostfxr, etc.) into the output directory, and
# publishing two different projects into the same folder back-to-back would have this pass's files
# collide with (and potentially get partially overwritten by) the Node publish above. Only the one
# binary this pass actually produces gets copied over.
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

if (-not $SkipVision) {
    Write-Step "Publishing LarisVMS.Vision.Service ($Platform, self-contained, single-file, all ONNX Runtime backends)"
    # Own temp folder, same reasoning as the NodeUpdater publish above — a self-contained single-file
    # publish drops its own copy of shared runtime files, which would collide with Node's if published
    # straight into $pkgOut.
    $visionTmp = Join-Path $OutputRoot "$Platform-vision-tmp"
    if (Test-Path $visionTmp) { Remove-Item $visionTmp -Recurse -Force }
    dotnet publish $VisionProject `
        -c $Configuration `
        -r $Platform `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:NoWarn=CA1416 `
        "-p:Version=$fullVersion" `
        @publisherProps `
        -o $visionTmp
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    # Everything from this publish, not just the .exe — the managed ONNX Runtime assembly plus the
    # onnxruntime-backends\{cuda,directml,openvino}\ tree LarisVMS.Vision.Service.csproj's
    # StageOnnxBackends target lays down (each backend's own onnxruntime.dll + provider natives, and
    # the OpenVINO plugin with its bundled runtime), all beside the exe
    # rather than inside the single file.
    Copy-Item (Join-Path $visionTmp '*') $pkgOut -Recurse -Force
    Remove-Item $visionTmp -Recurse -Force
    if (-not (Test-Path (Join-Path $pkgOut 'onnxruntime-backends'))) {
        throw "Vision publish produced no onnxruntime-backends\ folder — StageOnnxBackends did not run. AI detection would not work on any node."
    }
    Write-Ok "Published (ONNX Runtime backends bundled)"

    # onnxruntime_providers_cuda.dll is ~320 MB and only NVIDIA nodes ever load it — pull it OUT of
    # the package into a sibling folder the Web package bundles for the server to serve, so a
    # node that resolves CUDA downloads it once instead of it bloating every CPU/DirectML install.
    # See CudaProviderProvisioner. The small CUDA files (onnxruntime.dll, providers_shared/tensorrt)
    # stay in the package so the resolver can still see a cuda\ folder.
    # Exactly LarisVMS.Core.CudaProviderFiles: the file name per OS, and the name its signature covers.
    $cudaProviderName = if ($isWindowsBuild) { 'onnxruntime_providers_cuda.dll' } else { 'libonnxruntime_providers_cuda.so' }
    $cudaSignedName = if ($isWindowsBuild) { 'cuda-provider' } else { "cuda-provider-$Platform" }
    $cudaProviderDll = Join-Path $pkgOut "onnxruntime-backends\cuda\$cudaProviderName"
    if (Test-Path $cudaProviderDll) {
        $cudaProviderOut = Join-Path $metaRoot 'cuda-provider'
        if (Test-Path $cudaProviderOut) { Remove-Item $cudaProviderOut -Recurse -Force }
        New-Item -ItemType Directory -Path $cudaProviderOut -Force | Out-Null
        Move-Item $cudaProviderDll (Join-Path $cudaProviderOut $cudaProviderName) -Force
        $sizeMb = [math]::Round((Get-Item (Join-Path $cudaProviderOut $cudaProviderName)).Length / 1MB)
        Write-Ok "CUDA provider ($sizeMb MB) split out to $cudaProviderOut (not shipped in the package; the Web package bundles it for the server)"
        if ($PublisherKeyPath) {
            # Exactly UpdateSignature.NativePayload: nodes built with the publisher key install it only
            # with this signature too (CudaProviderProvisioner).
            $cudaHash = (Get-FileHash (Join-Path $cudaProviderOut $cudaProviderName) -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-PublisherSignature "larisvms-native:v1|$cudaSignedName|$cudaHash" (Join-Path $cudaProviderOut "$cudaProviderName.publisher.sig")
        }
    } else {
        Write-Host "    $cudaProviderName not found in the CUDA backend — CUDA acceleration won't be available on any node." -ForegroundColor Yellow
    }

    # The native backend libraries, for pushed updates (BackendsBundle): stamp the tree (the MSI and
    # install-node.ps1 carry the stamp too, so a fresh install matches the bundle), then zip it for
    # the server to offer beside the Vision Service binary. A node downloads it only when its own
    # stamp differs, so an update that doesn't change ONNX Runtime costs nothing extra.
    $backendsDir = Join-Path $pkgOut 'onnxruntime-backends'
    $backendsStamp = Get-BackendsStamp $backendsDir
    Set-Content -Path (Join-Path $backendsDir '.stamp') -Value $backendsStamp -NoNewline
    $backendsOut = Join-Path $metaRoot 'backends'
    if (Test-Path $backendsOut) { Remove-Item $backendsOut -Recurse -Force }
    New-Item -ItemType Directory -Path $backendsOut -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($backendsDir, (Join-Path $backendsOut 'onnxruntime-backends.zip'),
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Set-Content -Path (Join-Path $backendsOut 'backends-stamp.txt') -Value $backendsStamp -NoNewline
    $zipMb = [math]::Round((Get-Item (Join-Path $backendsOut 'onnxruntime-backends.zip')).Length / 1MB)
    Write-Ok "Native backend libraries zipped for pushed updates ($zipMb MB, stamp $($backendsStamp.Substring(0, 12)))"
    if ($PublisherKeyPath) {
        # Exactly UpdateSignature.BackendsPayload.
        Write-PublisherSignature "larisvms-backends:v1|$Platform|$fullVersion|$backendsStamp" (Join-Path $backendsOut 'backends.publisher.sig')
    }

    Write-Host "    Models are not bundled into the package — drop .onnx files (with an optional same-basename .json sidecar) into C:\ProgramData\LarisVMS\models on the node itself. See tools/export-models/ for how to obtain one." -ForegroundColor Yellow
} else {
    Write-Step "Skipping LarisVMS.Vision.Service (-SkipVision) — this package will be recording-only, no AI detection."
}

if ($PublisherKeyPath) {
    # Exactly UpdateSignature.UpdatePayload: platform, version, and both binaries' SHA-256 (the
    # Vision Service's empty when this build has none), as the server will offer them.
    $nodeHash = (Get-FileHash (Join-Path $pkgOut "LarisVMS.Node$exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    $visionExe = Join-Path $pkgOut "LarisVMS.Vision.Service$exe"

    $visionHash = if (Test-Path $visionExe) { (Get-FileHash $visionExe -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    Write-PublisherSignature "larisvms-update:v1|$Platform|$fullVersion|$nodeHash|$visionHash" $publisherSignaturePath
}

if (-not $isWindowsBuild) {
    # A Linux build is packaged by build-linux-packages.ps1 (called by build-installers.ps1).
    Write-Host "`nLinux build ready: $pkgOut (package it with build-linux-packages.ps1)" -ForegroundColor Yellow
    Write-Host "`nDone." -ForegroundColor Green
    return
}

Copy-Item (Join-Path $PSScriptRoot 'install-node.ps1') $pkgOut -Force
Write-Ok "install-node.ps1 bundled"

if ($ExtraPublishPath) {
    Write-Step "Mirroring package to $ExtraPublishPath"
    New-Item -ItemType Directory -Path $ExtraPublishPath -Force | Out-Null
    # /MIR so a stale file from a previous build (e.g. an old ffmpeg DLL that's no longer bundled)
    # doesn't linger alongside the new one; /R:2 /W:2 keeps a transient share hiccup from hanging
    # the whole build instead of failing fast.
    robocopy $pkgOut $ExtraPublishPath /MIR /R:2 /W:2 /NFL /NDL /NJH | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy to '$ExtraPublishPath' failed (exit $LASTEXITCODE)." }
    Write-Ok "Mirrored to $ExtraPublishPath"
}

Write-Host "`nPackage ready: $pkgOut" -ForegroundColor Yellow
if ($ExtraPublishPath) {
    Write-Host "Also mirrored to: $ExtraPublishPath" -ForegroundColor Yellow
    Write-Host "On the recorder machine (as Administrator), from that path:" -ForegroundColor DarkYellow
} else {
    Write-Host "Copy this folder to the recorder machine, then on that machine (as Administrator):" -ForegroundColor DarkYellow
}
Write-Host "  .\install-node.ps1 -ServerUrl <url> -RegistrationKey <key>" -ForegroundColor DarkYellow
Write-Host "`nDone." -ForegroundColor Green
