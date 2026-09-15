<#
.SYNOPSIS
    Install a recorder node (LarisVMS.Node) as a Windows Service.

.DESCRIPTION
    Installs the pre-built LarisVMS.Node.exe (expected in the same folder as this script — see
    build-node.ps1) to $InstallDir, registers it as a Windows Service with the registration
    arguments baked into the service's command line, configures automatic restart on failure, and
    starts it.

    On first start the service registers itself with LarisVMS.Web using -RegistrationKey (from
    Admin -> Nodes, or the Setup wizard's Node step) and persists the assigned NodeId/secret to
    %ProgramData%\LarisVMS\node.config (DPAPI-protected, LocalMachine scope). Every subsequent start
    — including a re-run of this script to change settings — loads that file and skips
    re-registration, so re-running this script is safe.

    Re-running against an already-installed node (upgrade) stops the existing service rather than
    deleting and recreating it.

    ffmpeg is NOT bundled or copied anywhere by this script. Install it first — recommended:
    `winget install ffmpeg --scope machine` — and the node discovers it on PATH or under the WinGet
    package store at every startup (so an ffmpeg upgrade is picked up with no re-install here). This
    script only preflight-checks that it's present; pass -FfmpegPath to point at a copy that neither
    PATH nor the WinGet store would surface.

    Storage access: if the storage root is a network share (\\server\share\...), the account the
    service runs as needs read/write access to it. The default LocalSystem account can still work
    for this — Windows authenticates LocalSystem to the network as the machine's own computer
    account (DOMAIN\COMPUTERNAME$) — but only once that computer account has been explicitly
    granted share and NTFS permissions on the target; it has none by default. Pass
    -ServiceCredential for a domain/service account instead if you'd rather manage access that way,
    or the share doesn't support computer-account auth (e.g. a workgroup/non-domain NAS).

.EXAMPLE
    .\install-node.ps1 -ServerUrl "https://larisvms.example.com" -RegistrationKey "abc123"
    .\install-node.ps1 -ServerUrl "https://larisvms.example.com" -RegistrationKey "abc123" -InsecureTls
    .\install-node.ps1 -ServerUrl "https://larisvms.example.com" -RegistrationKey "abc123" -ServiceCredential (Get-Credential)
#>

param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$RegistrationKey,
    [string]$BinaryPath        = (Join-Path $PSScriptRoot 'LarisVMS.Node.exe'),
    # Recorder-node auto-update's binary-swap helper (see LarisVMS.Node/Update/UpdateService.cs) —
    # published alongside LarisVMS.Node.exe by build-node.ps1, and must already be present in
    # $InstallDir *before* an update is ever triggered, since UpdateService looks for it next to the
    # currently-running exe and just logs a warning and skips the update if it's missing.
    [string]$UpdaterBinaryPath = (Join-Path $PSScriptRoot 'LarisVMS.NodeUpdater.exe'),
    # LarisVMS.Vision.Service.exe + the onnxruntime-backends\ tree (every ONNX Runtime backend) +
    # models\ — all optional (build-node.ps1 -SkipVision omits them entirely), a plain recording-only
    # node needs none of it. NodeWorker's own VisionServiceSupervisor looks for this exact fixed
    # filename in its own install directory (AppContext.BaseDirectory) at runtime, so every sibling
    # file next to it here (onnxruntime-backends\, models\) has to land in $InstallDir too.
    [string]$VisionBinaryPath  = (Join-Path $PSScriptRoot 'LarisVMS.Vision.Service.exe'),
    [string]$InstallDir        = 'C:\Program Files\LarisVMS\Node',
    [string]$ServiceName       = 'LarisVMSNode',
    [string]$ServiceDisplay    = 'LarisVMS Node',
    # Optional override. ffmpeg is not bundled — install it (recommended:
    # `winget install ffmpeg --scope machine`) and both this script and the node's own
    # FfmpegPathResolver discover it on PATH or under the WinGet package store. Pass this only to
    # point at a copy neither would find; when set it is baked into the service command line as
    # --ffmpeg-path, otherwise the node re-discovers at every startup (so an ffmpeg upgrade needs no
    # re-install here).
    [string]$FfmpegPath        = '',
    # Where this node records to. Storage config is per-node (there is no global default) — pass this
    # when installing a node, or set it later on Admin -> Nodes. A node installed without it records
    # nothing until a path is set. Sent to the server at registration and baked into the service
    # command line as --storage-root.
    [string]$StorageRoot       = '',
    # Optional secondary volume (SMB share / USB drive) this node archives aged-out footage to when
    # archiving is enabled. Must be a separate location from -StorageRoot. Sent at registration;
    # not baked into the command line (archive root comes from server config at runtime).
    [string]$ArchiveRoot       = '',
    [switch]$InsecureTls,
    [pscredential]$ServiceCredential,
    # M5 live view: the node's own Kestrel port, reached only by LarisVMS.Web's server-side proxy over
    # the LAN (plain HTTP, never by a browser directly — see the plan's "Media path" section). Needs
    # an inbound firewall allow rule, added below, or LarisVMS.Web can reach the port but every
    # connection attempt just hangs until it times out — confirmed on a real node.
    [int]$LivePort             = 8554,

    # ── Failover plan phase 1: direct-to-node client HTTPS endpoint ────────────────────────────────
    # 0 (the default) leaves it off. A non-zero port stands up a second Kestrel listener that a
    # browser can be pointed straight at for live/playback (skipping the central relay), and opens an
    # inbound firewall rule for it. Written to %ProgramData%\LarisVMS\client-endpoint.json, which
    # overrides anything the server pushes.
    [int]$ClientPort           = 0,
    # Path to the .pfx the client endpoint should present, and its password. Omit both and pass
    # -ClientAllowInsecure to have the node auto-generate a stable self-signed certificate instead
    # (setup/testing only). A path/password set on Admin -> Nodes is used when these are omitted.
    [string]$ClientPfxPath     = '',
    [string]$ClientPfxPassword = '',
    # Let the client endpoint come up on a self-signed certificate — viewers click through a browser
    # warning. Setup/testing only; also requires the matching global toggle on Admin -> Settings ->
    # Live View before the server will actually route browsers to it.
    [switch]$ClientAllowInsecure,
    # FQDN browsers use to reach this node's client endpoint (must match the certificate). Defaults to
    # the machine name; also settable on Admin -> Nodes.
    [string]$ClientEndpointHost = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

function Format-ServiceArg([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}

# Windows does not kill child processes when their parent dies or is stopped — LarisVMS.Node's own
# graceful shutdown is supposed to kill each ffmpeg.exe it spawned, but if the SCM's stop timeout is
# hit before that finishes (confirmed happening on a real node), ffmpeg.exe is left running,
# orphaned, and still holding its DLLs open, which fails a same-path copy with "being used by
# another process". An earlier version of this function tried to filter to only ffmpeg processes
# under this node's install dir by checking each process's .Path — that's the wrong tool here:
# querying .Path on a process running as a different account (the service, and therefore its
# ffmpeg.exe children, normally run as LocalSystem) can silently fail even from an elevated
# Administrator session, which meant the filter matched nothing and killed nothing. ffmpeg is only
# ever run by LarisVMS.Node on this machine, so unconditionally stopping every ffmpeg.exe still around
# after the service reports Stopped is both safe and reliable.
function Stop-OrphanedFfmpeg {
    $procs = @(Get-Process -Name ffmpeg -ErrorAction SilentlyContinue)
    if ($procs) {
        Write-Host "    Found $($procs.Count) orphaned ffmpeg process(es) still running - stopping..."
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

# Same reasoning as Stop-OrphanedFfmpeg above — LarisVMS.Vision.Service.exe is a child process
# NodeWorker's own VisionServiceSupervisor launches, and Windows doesn't kill children when their
# parent service is stopped. Node's own graceful shutdown is supposed to stop it first, but if the
# SCM's stop timeout is hit before that finishes, it's left running and holding its own exe/DLLs open,
# which fails the copy below with "being used by another process."
function Stop-OrphanedVisionService {
    $procs = @(Get-Process -Name 'LarisVMS.Vision.Service' -ErrorAction SilentlyContinue)
    if ($procs) {
        Write-Host "    Found $($procs.Count) orphaned LarisVMS.Vision.Service process(es) still running - stopping..."
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

# Belt-and-suspenders on top of Stop-OrphanedFfmpeg above: a just-killed process's file handles can
# take the OS a moment longer to fully release even after Stop-Process returns, so a same-path copy
# right afterward can still transiently fail. Retrying beats making the whole upgrade fail on a race
# that clears itself within a couple of seconds.
function Copy-ItemWithRetry([string]$Source, [string]$Destination, [int]$MaxAttempts = 5) {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-Item $Source $Destination -Recurse -Force
            return
        } catch {
            if ($attempt -eq $MaxAttempts) { throw }
            Write-Host "    Copy attempt $attempt/$MaxAttempts failed (file still in use) - retrying in 2s..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
}

# ── AI detection native dependencies ─────────────────────────────────────────
# The node package now bundles every ONNX Runtime backend (onnxruntime-backends\{cuda,directml,
# openvino,cpu}\) and VisionBackendResolver picks one at runtime for this machine's hardware. Only
# the CUDA backend has prerequisites that aren't in the package: the CUDA Toolkit 12.x runtime and
# cuDNN 9.x. DirectML and OpenVINO need nothing but a current GPU driver, and the resolver falls
# back CUDA -> DirectML -> CPU on its own — so a node with an NVIDIA GPU but no toolkit still does
# GPU-accelerated detection via DirectML; installing the toolkit just unlocks the faster CUDA path.
#
# This only does anything on a machine with an NVIDIA GPU. It checks for the CUDA runtime + cuDNN up
# front (the OS loader reports exactly one missing DLL at a time, so finding them by running the
# thing is a fix-restart-retry cycle per library), and for anything missing goes looking in the
# places these libraries actually land — neither installs anywhere the loader searches by default:
# `pip install nvidia-cudnn-cu12` goes to a Python env's site-packages, NVIDIA's own cuDNN download
# is a zip you unpack wherever. When found they're copied into onnxruntime-backends\cuda\ (beside
# that backend's onnxruntime.dll, which the loader searches first) — a copy rather than a PATH edit,
# since a Windows Service inherits its environment from services.exe at boot. When not found, the
# node is told what to install and that it's running DirectML meanwhile.
#
# Never fatal: the same "AI detection is additive" reasoning as the rest of this release.

# Where the OS loader would actually find a DLL for LarisVMS.Vision.Service.exe: its own directory
# first, then the standard system directories, then PATH. The machine PATH is read from the registry
# in addition to this process's own copy — a shell opened before the CUDA Toolkit installer ran holds
# a stale $env:PATH, which would otherwise produce a false "missing" for a library that is in fact
# installed correctly.
function Get-NativeDllSearchDirectories([string]$InstallDirectory) {
    $dirs = [System.Collections.Generic.List[string]]::new()
    $dirs.Add($InstallDirectory)
    $dirs.Add([Environment]::SystemDirectory)
    foreach ($scope in @('Process', 'Machine')) {
        $raw = [Environment]::GetEnvironmentVariable('Path', $scope)
        if ($raw) { foreach ($entry in $raw -split ';') { if ($entry.Trim()) { $dirs.Add($entry.Trim()) } } }
    }
    return $dirs
}

function Test-NativeDllAvailable([string]$DllName, $SearchDirectories) {
    foreach ($dir in $SearchDirectories) {
        # A malformed PATH entry (stray quotes, invalid characters) makes Join-Path throw rather than
        # return nothing — one bad entry must not abort the whole check.
        try {
            if (Test-Path (Join-Path $dir $DllName)) { return $true }
        } catch { continue }
    }
    return $false
}

# Directories a missing CUDA/cuDNN library plausibly lives in, newest first. Wildcards throughout:
# the version is in the path for every one of these layouts, and pinning a specific version here
# would mean this stops finding a perfectly good install the moment anyone upgrades. Ordered so the
# CUDA Toolkit's own bin wins over a pip copy — the toolkit is the version-matched one when both
# exist, and this fleet has hit a node carrying toolkit 12.8 alongside pip's CUDA 12.9 builds.
function Get-NativeDllSourceDirectories {
    $roots = @(
        # CUDA Toolkit (cudart/cublas/cublasLt/cufft), and cuDNN if it was unpacked into the toolkit
        "$env:ProgramFiles\NVIDIA GPU Computing Toolkit\CUDA\v*\bin"
        # NVIDIA's standalone cuDNN installer/zip — bin\12.x on 9.x layouts, plain bin on older ones
        "$env:ProgramFiles\NVIDIA\CUDNN\v*\bin\*"
        "$env:ProgramFiles\NVIDIA\CUDNN\v*\bin"
        # pip: nvidia-cudnn-cu12, nvidia-cublas-cu12, nvidia-cuda-runtime-cu12, nvidia-cufft-cu12.
        # Machine-wide, per-user, and venv installs all land in a different place.
        "$env:ProgramFiles\Python*\Lib\site-packages\nvidia\*\bin"
        "$env:LOCALAPPDATA\Programs\Python\Python*\Lib\site-packages\nvidia\*\bin"
        "$env:APPDATA\Python\Python*\site-packages\nvidia\*\bin"
    )

    $found = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $roots) {
        # -Directory so a wildcard that happens to match a file can't end up treated as a directory.
        $hits = @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
        foreach ($hit in $hits) { $found.Add($hit.FullName) }
    }
    return $found
}

# Copies one missing library in from wherever it was found. cuDNN is special-cased: cudnn64_9.dll is
# a thin dispatcher that loads cudnn_graph64_9.dll / cudnn_ops64_9.dll / cudnn_engines_*64_9.dll at
# runtime, so copying only the DLL named in the loader error just moves the failure to the next one —
# every cudnn*.dll from the same directory has to come along. Returns $true if the copy happened.
function Copy-NativeDllFromSource([string]$DllName, [string]$InstallDirectory, $SourceDirectories) {
    foreach ($dir in $SourceDirectories) {
        $candidate = Join-Path $dir $DllName
        if (-not (Test-Path $candidate)) { continue }

        $pattern = if ($DllName -like 'cudnn*') { 'cudnn*.dll' } else { $DllName }
        try {
            Copy-ItemWithRetry (Join-Path $dir $pattern) $InstallDirectory
            Write-Host "    Copied $pattern from $dir" -ForegroundColor DarkGray
            return $true
        } catch {
            Write-Host "    Found $DllName in $dir but could not copy it: $($_.Exception.Message)" -ForegroundColor Yellow
            return $false
        }
    }
    return $false
}

# The package bundles every ONNX Runtime backend, so there is no "which variant" to detect — the
# only machine-specific setup is CUDA, and only on a box that actually has an NVIDIA GPU.
function Test-NvidiaGpuPresent {
    $names = @()
    try { $names = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object -ExpandProperty Name) }
    catch {
        try { $names = @(Get-WmiObject Win32_VideoController -ErrorAction Stop | Select-Object -ExpandProperty Name) }
        catch { return $false }
    }
    foreach ($n in $names) { if ($n -match 'NVIDIA') { return $true } }
    return $false
}

function Test-VisionModelsPresent([string]$InstallDirectory) {
    $modelsDir = Join-Path $InstallDirectory 'models'
    $models = @()
    if (Test-Path $modelsDir) { $models = @(Get-ChildItem $modelsDir -Filter '*.onnx' -File -ErrorAction SilentlyContinue) }
    if ($models.Count -eq 0) {
        Write-Host "    No .onnx model bundled - the Vision Service fetches the model it needs from the server on first use." -ForegroundColor DarkGray
    } else {
        Write-Ok "$($models.Count) model file(s) bundled"
    }
}

function Install-VisionNativeDependencies([string]$InstallDirectory) {
    $cudaBackendDir = Join-Path $InstallDirectory 'onnxruntime-backends\cuda'

    if (-not (Test-NvidiaGpuPresent)) {
        Write-Step "Checking AI detection dependencies"
        Write-Ok "no NVIDIA GPU detected - this node runs DirectML or CPU, which need no extra libraries"
        Test-VisionModelsPresent $InstallDirectory
        return
    }

    Write-Step "Checking AI detection dependencies (NVIDIA GPU detected - checking the CUDA path)"

    $required = @(
        @{ Dll = 'cudart64_12.dll';    Provides = 'CUDA Toolkit 12.x' }
        @{ Dll = 'cublas64_12.dll';    Provides = 'CUDA Toolkit 12.x' }
        @{ Dll = 'cublasLt64_12.dll';  Provides = 'CUDA Toolkit 12.x' }
        @{ Dll = 'cufft64_11.dll';     Provides = 'CUDA Toolkit 12.x' }
        @{ Dll = 'cudnn64_9.dll';      Provides = 'cuDNN 9.x for CUDA 12' }
    )

    $searchDirs = @($cudaBackendDir) + @(Get-NativeDllSearchDirectories $InstallDirectory)
    $missing = @($required | Where-Object { -not (Test-NativeDllAvailable $_.Dll $searchDirs) })

    # Only pay for the source scan (globbing several Program Files/Python trees) when something is
    # actually missing — the common re-run case is a node that's already fully set up.
    if ($missing.Count -gt 0) {
        if (-not (Test-Path $cudaBackendDir)) { New-Item -ItemType Directory -Path $cudaBackendDir -Force | Out-Null }
        $sourceDirs = Get-NativeDllSourceDirectories
        $stillMissing = [System.Collections.Generic.List[hashtable]]::new()
        foreach ($item in $missing) {
            # Copied next to the CUDA backend's own onnxruntime.dll — where VisionBackendResolver
            # prepends to PATH and where the loader searches first.
            if (-not (Copy-NativeDllFromSource $item.Dll $cudaBackendDir $sourceDirs)) {
                $stillMissing.Add($item)
            }
        }
        $searchDirs = @($cudaBackendDir) + @(Get-NativeDllSearchDirectories $InstallDirectory)
        $missing = @($stillMissing | Where-Object { -not (Test-NativeDllAvailable $_.Dll $searchDirs) })
    }

    if ($missing.Count -eq 0) {
        Write-Ok "CUDA Toolkit runtime + cuDNN present - the node downloads the ~320 MB CUDA provider library from the server on first run, then uses CUDA"
    } else {
        Write-Host "NOTE: this node has an NVIDIA GPU but the CUDA path is not set up - it will run DirectML (still GPU-accelerated) instead." -ForegroundColor Yellow
        Write-Host "      Missing, and not found anywhere this script knows to look:" -ForegroundColor Yellow
        foreach ($item in $missing) {
            Write-Host "        $($item.Dll)  <- $($item.Provides)" -ForegroundColor Yellow
        }
        Write-Host "      Install on THIS machine (not the LarisVMS web server), then re-run this script to switch it to CUDA:" -ForegroundColor Yellow
        Write-Host "        CUDA Toolkit 12.8  https://developer.nvidia.com/cuda-12-8-0-download-archive" -ForegroundColor Yellow
        Write-Host "        cuDNN 9.x          https://developer.nvidia.com/cudnn" -ForegroundColor Yellow
        Write-Host "        cuDNN via pip:     pip install --extra-index-url https://pypi.nvidia.com nvidia-cudnn-cu12" -ForegroundColor Yellow
    }

    Test-VisionModelsPresent $InstallDirectory
}

# Locates an ffmpeg install without installing anything — mirrors LarisVMS.Media.FfmpegPathResolver
# so this script's preflight and the running node agree: an explicit path wins, then `ffmpeg` on
# PATH, then the newest ffmpeg.exe under a WinGet package folder (machine scope first, then
# per-user). Returns $null when nothing turns up.
function Find-Ffmpeg([string]$Explicit) {
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path $Explicit)) { throw "Specified -FfmpegPath does not exist: $Explicit" }
        return (Resolve-Path $Explicit).Path
    }

    $onPath = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    foreach ($root in @((Join-Path $env:ProgramFiles 'WinGet\Packages'),
                        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'))) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem -Path $root -Filter 'ffmpeg.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'ffmpeg' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
        if ($hit) { return $hit }
    }

    return $null
}

# ── pre-flight ────────────────────────────────────────────────────────────────

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator."
}

if (-not (Test-Path $BinaryPath)) {
    throw "Binary not found: $BinaryPath`nBuild it first with: .\build-node.ps1 (run on a dev machine, then copy the publish\LarisVMS.Node\win\ folder here)."
}

# Not fatal on its own — auto-update just stays inert (UpdateService logs a warning and skips) until
# it's present — but worth a loud heads-up here rather than a silent gap discovered only when the
# first update is ever attempted.
if (-not (Test-Path $UpdaterBinaryPath)) {
    Write-Host "WARNING: Updater binary not found: $UpdaterBinaryPath — this node will not be able to auto-update until LarisVMS.NodeUpdater.exe is installed (re-run this script once it's available)." -ForegroundColor Yellow
}

# Not fatal — a package built with build-node.ps1 -SkipVision, or a plain recording-only node that
# doesn't want AI detection at all, legitimately has no Vision Service here. AI detection stays
# unavailable on this node (every other detection path is unaffected) until it's installed.
$hasVision = Test-Path $VisionBinaryPath
if (-not $hasVision) {
    Write-Host "NOTE: LarisVMS.Vision.Service.exe not found: $VisionBinaryPath — this node will run recording-only, with no AI object detection available, until it's installed." -ForegroundColor Yellow
}

# ── stop existing service ────────────────────────────────────────────────────
# Must happen before anything below touches $InstallDir: a running node's ffmpeg.exe/DLLs and its
# own LarisVMS.Node.exe can be locked by the currently-running process, so overwriting them while the
# old service is still up (the normal case for an in-place upgrade of a live recorder) can fail
# mid-copy. Stopping first — even on a fresh install where $existingSvc is null and this is a no-op —
# guarantees every copy below lands on an unlocked target.
#
# Deliberately stop-only, not delete+recreate: an upgrade updates the same ServiceName in place
# (possibly with changed arguments) further down instead of tearing the service down and rebuilding
# it — less disruptive to anything referencing the service (monitoring, Event Viewer history) and
# there's no reason deleting it was ever necessary for this.

$isUpgrade = $false
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    $isUpgrade = $true
    Write-Step "Stopping existing service '$ServiceName'"
    if ($existingSvc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existingSvc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Write-Ok "Stopped"
    }
    Stop-OrphanedFfmpeg
    Stop-OrphanedVisionService
}

# ── verify ffmpeg is installed ───────────────────────────────────────────────
# Not bundled or copied anywhere by this script. The operator installs ffmpeg
# (`winget install ffmpeg --scope machine`), and the node's own FfmpegPathResolver re-discovers it
# on PATH or under the WinGet package store at every startup — so an ffmpeg upgrade is picked up
# with no re-run here. This is only a fail-fast preflight; --ffmpeg-path is baked into the service
# command line further down only when -FfmpegPath was passed explicitly.

Write-Step "Checking for ffmpeg"
$resolvedFfmpeg = Find-Ffmpeg $FfmpegPath
if (-not $resolvedFfmpeg) {
    throw @"
ffmpeg was not found (checked PATH and the WinGet package store).
Install it, then re-run this script:

    winget install ffmpeg --scope machine

Or pass -FfmpegPath 'C:\path\to\ffmpeg.exe' to point at an existing copy.
"@
}
& $resolvedFfmpeg -version *> $null
if ($LASTEXITCODE -ne 0) { throw "ffmpeg was found at '$resolvedFfmpeg' but 'ffmpeg -version' failed (exit $LASTEXITCODE)." }
Write-Ok "Found: $resolvedFfmpeg"

# ── install files ─────────────────────────────────────────────────────────────

Write-Step "Installing node to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-ItemWithRetry $BinaryPath (Join-Path $InstallDir 'LarisVMS.Node.exe')
if (Test-Path $UpdaterBinaryPath) {
    Copy-ItemWithRetry $UpdaterBinaryPath (Join-Path $InstallDir 'LarisVMS.NodeUpdater.exe')
}
if ($hasVision) {
    # Everything the package ships beside LarisVMS.Vision.Service.exe: the exe, the loose managed
    # assemblies (Microsoft.ML.OnnxRuntime.dll, YoloDotNet, ...), and the onnxruntime-backends\ tree
    # (every native ONNX Runtime build — cuda/directml/openvino/cpu — each with its own onnxruntime.dll
    # and provider natives; VisionBackendResolver picks one at runtime). Models are handled separately
    # below — they are never bundled, so there is no models\ in the package to copy.
    $visionSourceDir = Split-Path $VisionBinaryPath -Parent
    Copy-ItemWithRetry (Join-Path $visionSourceDir 'LarisVMS.Vision.Service.*') $InstallDir
    Copy-ItemWithRetry (Join-Path $visionSourceDir '*.dll') $InstallDir
    $backendsSourceDir = Join-Path $visionSourceDir 'onnxruntime-backends'
    if (Test-Path $backendsSourceDir) {
        Copy-ItemWithRetry $backendsSourceDir $InstallDir
    } else {
        Write-Host "WARNING: onnxruntime-backends\ not found beside LarisVMS.Vision.Service.exe - AI detection will not run. Rebuild the package with build-node.ps1." -ForegroundColor Yellow
    }
    # Models are never bundled into the package (see build-node.ps1) — they live in this shared,
    # writable, per-machine location instead, so an upgrade never overwrites an operator-supplied
    # model and a fresh install always has somewhere to drop one into.
    $modelsDir = Join-Path $env:ProgramData 'LarisVMS\models'
    New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
    if (-not (Get-ChildItem $modelsDir -Filter '*.onnx' -File -ErrorAction SilentlyContinue)) {
        Write-Host "    No .onnx models found in $modelsDir yet — AI detection won't have anything to run until you drop one in (with an optional same-basename .json sidecar if its own metadata isn't enough). See tools/export-models/ for how to obtain one." -ForegroundColor Yellow
    }
}
Write-Ok "Files installed"

# Here rather than after the service starts: this copies DLLs into $InstallDir, and the service (plus
# the LarisVMS.Vision.Service.exe child it launches) holds those exact files open once running —
# exactly the "being used by another process" copy failure the stop-first sequencing above exists to
# avoid. A -SkipVision/recording-only package has no AI detection to have dependencies for.
if ($hasVision) {
    Install-VisionNativeDependencies $InstallDir
}

# ── register / update service ────────────────────────────────────────────────
# Registration/heartbeat arguments are baked into the service's own command line rather than a
# config file: Program.cs already accepts them as CLI args (and LARISVMS_* environment variables) for
# interactive testing, so reusing that same surface here needs no extra code path. They're only
# actually used on the very first start — once node.config exists, Program.cs skips registration
# and just loads the persisted NodeId/secret, so re-running this script to rotate, say, the ffmpeg
# path is safe and won't re-register a second node.

$exePath = Join-Path $InstallDir 'LarisVMS.Node.exe'
# --ffmpeg-path is passed only when -FfmpegPath was given explicitly; otherwise the node's own
# FfmpegPathResolver discovers ffmpeg (PATH / WinGet package store) at every startup, so an ffmpeg
# upgrade needs no re-run of this script.
$argParts = @('--server-url', $ServerUrl, '--registration-key', $RegistrationKey, '--live-port', $LivePort)
if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) { $argParts += @('--ffmpeg-path', $resolvedFfmpeg) }
if ($StorageRoot) { $argParts += @('--storage-root', $StorageRoot) }
# --archive-root is read only during first-run registration (to persist it on the Node row); after
# that the archive root comes from server config, so it's still safe to bake in but not required.
if ($ArchiveRoot) { $argParts += @('--archive-root', $ArchiveRoot) }
if ($InsecureTls) { $argParts += '--insecure-tls' }

$binPath = (Format-ServiceArg $exePath) + ' ' + (($argParts | ForEach-Object { Format-ServiceArg $_ }) -join ' ')

if ($ServiceCredential) {
    Write-Host "    Running as $($ServiceCredential.UserName) (needed for network/SMB storage access)."
} else {
    Write-Host "    Running as LocalSystem." -ForegroundColor Yellow
    Write-Host "    If the storage root is a network share, LocalSystem authenticates to it as this" -ForegroundColor Yellow
    Write-Host "    machine's own computer account ($env:COMPUTERNAME`$) - make sure that account has" -ForegroundColor Yellow
    Write-Host "    share and NTFS permissions on the target, or recording will fail. Re-run with" -ForegroundColor Yellow
    Write-Host "    -ServiceCredential (Get-Credential) instead if you'd rather use a domain/service account." -ForegroundColor Yellow
}

if ($isUpgrade) {
    # Updates the existing service's binary path/display name/credential in place via WMI
    # (Win32_Service.Change) rather than `sc.exe config` — sc.exe's `key= value` argument syntax and
    # $binPath's own embedded quoting (from Format-ServiceArg, needed for the exe path and each
    # argument that itself contains spaces) is a well-known minefield for PowerShell's native-command
    # argument passing, especially on Windows PowerShell 5.1. Change() takes PathName as a normal
    # .NET string parameter, so none of that applies. Any Change() parameter left out of $changeArgs
    # (ServiceType, ErrorControl, Description, ...) is left as-is, not reset.
    Write-Step "Updating existing service '$ServiceName'"
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    $changeArgs = @{
        DisplayName = $ServiceDisplay
        PathName    = $binPath
        StartMode   = 'Automatic'
    }
    if ($ServiceCredential) {
        $changeArgs['StartName'] = $ServiceCredential.UserName
        $changeArgs['StartPassword'] = $ServiceCredential.GetNetworkCredential().Password
    }
    $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $changeArgs
    if ($result.ReturnValue -ne 0) {
        throw "Failed to update service '$ServiceName' (Win32_Service.Change ReturnValue=$($result.ReturnValue))."
    }
    Write-Ok "Updated"
} else {
    Write-Step "Registering Windows Service '$ServiceName'"
    $serviceParams = @{
        Name            = $ServiceName
        DisplayName     = $ServiceDisplay
        Description     = 'LarisVMS recorder node - supervises FFmpeg-based 24/7 camera recording.'
        BinaryPathName  = $binPath
        StartupType     = 'Automatic'
    }
    if ($ServiceCredential) { $serviceParams['Credential'] = $ServiceCredential }
    New-Service @serviceParams | Out-Null
    Write-Ok "Registered"
}

# ── configure service recovery ────────────────────────────────────────────────

Write-Step "Configuring service recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Write-Ok "Recovery configured (restart on 1st/2nd/3rd failure; reset after 1 day)"

# ── firewall ──────────────────────────────────────────────────────────────────
# Without this, LarisVMS.Web's live-view proxy can open a TCP connection to $LivePort just fine (the
# handshake reaches Windows) but every WebSocket request hangs until it times out rather than failing
# fast — confirmed on a real node missing this rule. Named and re-created idempotently so re-running
# this script to change -LivePort updates the rule instead of leaving a stale one alongside the new one.
Write-Step "Configuring firewall for live view (TCP $LivePort)"
$firewallRuleName = "LarisVMS Node Live View"
Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $LivePort | Out-Null
Write-Ok "Allowed inbound TCP $LivePort"

# ── Failover plan phase 1: direct-to-node client HTTPS endpoint ────────────────────────────────────
$clientEndpointRuleName = "LarisVMS Node Client Endpoint"
Get-NetFirewallRule -DisplayName $clientEndpointRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
if ($ClientPort -gt 0) {
    Write-Step "Writing client-endpoint.json and firewall rule (TCP $ClientPort)"
    $clientCfgDir = Join-Path $env:ProgramData 'LarisVMS'
    New-Item -ItemType Directory -Force -Path $clientCfgDir | Out-Null
    $clientCfg = [ordered]@{
        enabled       = $true
        port          = $ClientPort
        allowInsecure = [bool]$ClientAllowInsecure
    }
    if ($ClientPfxPath)      { $clientCfg.pfxPath = $ClientPfxPath }
    if ($ClientPfxPassword)  { $clientCfg.pfxPassword = $ClientPfxPassword }
    if ($ClientEndpointHost) { $clientCfg.host = $ClientEndpointHost }
    $clientCfg | ConvertTo-Json | Set-Content -Path (Join-Path $clientCfgDir 'client-endpoint.json') -Encoding UTF8
    New-NetFirewallRule -DisplayName $clientEndpointRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $ClientPort | Out-Null
    Write-Ok "Client endpoint on TCP $ClientPort ($(if ($ClientAllowInsecure) { 'self-signed / insecure' } elseif ($ClientPfxPath) { 'supplied certificate' } else { 'certificate from server config' }))"
}
elseif ($ClientAllowInsecure -or $ClientPfxPath) {
    Write-Host "    -ClientAllowInsecure / -ClientPfxPath ignored: pass -ClientPort to enable the direct client endpoint." -ForegroundColor Yellow
}

# ── start ─────────────────────────────────────────────────────────────────────

Write-Step "Starting service '$ServiceName'"
Start-Service -Name $ServiceName
Write-Ok "Started"

Write-Host "`nNode $(if ($isUpgrade) { 'upgraded' } else { 'installed' }) successfully." -ForegroundColor Green
Write-Host "Logs:        Event Viewer > Windows Logs > Application  (source: $ServiceName)"
Write-Host "Install dir: $InstallDir"
Write-Host "Assign cameras to this node from Admin -> Nodes / the camera edit page in LarisVMS.Web."
