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
    deleting and recreating it, and reuses the ffmpeg already copied into $InstallDir\ffmpeg from a
    previous run instead of re-resolving it from PATH/winget/-FfmpegPath every time — pass
    -FfmpegPath explicitly only when you actually want to replace ffmpeg itself.

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
    # Object detection plan decisions 2/3: LarisVMS.Vision.Service.exe + its execution provider's own
    # native DLLs + models\ — all optional (build-node.ps1 -SkipVision omits them entirely), a plain
    # recording-only node needs none of it. NodeWorker's own VisionServiceSupervisor looks for this
    # exact fixed filename in its own install directory (AppContext.BaseDirectory) at runtime, so
    # every sibling file next to it here (native DLLs, models\) has to land in $InstallDir too, not
    # just the exe.
    [string]$VisionBinaryPath  = (Join-Path $PSScriptRoot 'LarisVMS.Vision.Service.exe'),
    [string]$InstallDir        = 'C:\Program Files\LarisVMS\Node',
    [string]$ServiceName       = 'LarisVMSNode',
    [string]$ServiceDisplay    = 'LarisVMS Node',
    [string]$FfmpegPath        = '',
    [switch]$InstallFfmpeg,
    [string]$StorageRoot       = '',
    [switch]$InsecureTls,
    [pscredential]$ServiceCredential,
    # M5 live view: the node's own Kestrel port, reached only by LarisVMS.Web's server-side proxy over
    # the LAN (plain HTTP, never by a browser directly — see the plan's "Media path" section). Needs
    # an inbound firewall allow rule, added below, or LarisVMS.Web can reach the port but every
    # connection attempt just hangs until it times out — confirmed on a real node.
    [int]$LivePort             = 8554
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
# LarisVMS.Vision.Service's execution provider loads native libraries that are NOT shipped in the
# node package — the CUDA Toolkit runtime and cuDNN for a Cuda build. Nothing about installing the
# node notices their absence on its own: the service starts fine, and the failure only appears
# later, per camera, per reconcile tick, as an ONNX Runtime "Error loading ... which depends on
# X.dll which is missing" buried in the Windows Application event log. Worse, the loader reports
# exactly ONE missing DLL at a time, so discovering them by running the thing means a
# fix-restart-retry cycle per library.
#
# So this checks all of them up front and, for anything missing, goes looking in the places these
# libraries actually land — neither installs anywhere the OS loader searches by default: cuDNN via
# `pip install nvidia-cudnn-cu12` goes to a Python environment's site-packages, and NVIDIA's own
# cuDNN download is a zip you unpack wherever you like. When found, they're copied in beside
# LarisVMS.Vision.Service.exe, which the loader searches first — deliberately a copy rather than a
# PATH edit, because a Windows Service inherits its environment from services.exe at boot and would
# not see a new PATH entry until the machine is rebooted.
#
# Never fatal: a node whose GPU libraries aren't set up yet still records perfectly well, and every
# non-AI detection path is unaffected — the same "AI detection is additive" reasoning as the rest of
# this release. Anything that can't be found is reported with where to get it.

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

# Which accelerator this package was built for is read from the native DLLs that actually shipped
# next to the exe, not passed in as a parameter: build-node.ps1's -Accel choice is baked into the
# published output (YoloDotNet links exactly one execution provider per build), and the operator
# running this script on a recorder often isn't the person who built the package and has no reliable
# way to know which variant they were handed.
function Install-VisionNativeDependencies([string]$InstallDirectory) {
    $providerDll = @{
        'onnxruntime_providers_cuda.dll' = 'Cuda'
        'DirectML.dll'                   = 'DirectML'
        'openvino.dll'                   = 'OpenVino'
    }
    $accel = 'Cpu'
    foreach ($dll in $providerDll.Keys) {
        if (Test-Path (Join-Path $InstallDirectory $dll)) { $accel = $providerDll[$dll]; break }
    }

    Write-Step "Checking AI detection dependencies (package built for: $accel)"

    # Only a Cuda build has system-level prerequisites. DirectML ships its own provider DLL in the
    # package and needs nothing but a current GPU driver; OpenVINO needs an Intel driver, which this
    # can't meaningfully probe for by file name; Cpu needs nothing at all.
    $required = @()
    if ($accel -eq 'Cuda') {
        $required = @(
            @{ Dll = 'cudart64_12.dll';    Provides = 'CUDA Toolkit 12.x' }
            @{ Dll = 'cublas64_12.dll';    Provides = 'CUDA Toolkit 12.x' }
            @{ Dll = 'cublasLt64_12.dll';  Provides = 'CUDA Toolkit 12.x' }
            @{ Dll = 'cufft64_11.dll';     Provides = 'CUDA Toolkit 12.x' }
            @{ Dll = 'cudnn64_9.dll';      Provides = 'cuDNN 9.x for CUDA 12' }
        )
    }

    $searchDirs = Get-NativeDllSearchDirectories $InstallDirectory
    $missing = @($required | Where-Object { -not (Test-NativeDllAvailable $_.Dll $searchDirs) })

    # Only pay for the source scan (globbing several Program Files/Python trees) when something is
    # actually missing — the common re-run case is a node that's already fully set up.
    if ($missing.Count -gt 0) {
        $sourceDirs = Get-NativeDllSourceDirectories
        $stillMissing = [System.Collections.Generic.List[hashtable]]::new()
        foreach ($item in $missing) {
            if (-not (Copy-NativeDllFromSource $item.Dll $InstallDirectory $sourceDirs)) {
                $stillMissing.Add($item)
            }
        }
        # Re-evaluated against the install directory rather than assumed from the copy results: a
        # cudnn*.dll copy pulls in siblings that may themselves have been on the missing list, so
        # asking the filesystem again is both simpler and more honest than tracking that by hand.
        $searchDirs = Get-NativeDllSearchDirectories $InstallDirectory
        $missing = @($stillMissing | Where-Object { -not (Test-NativeDllAvailable $_.Dll $searchDirs) })
    }

    # A model is required regardless of accelerator — a node with the runtime fully working but no
    # .onnx to load fails at exactly the same point, in exactly the same way.
    $modelsDir = Join-Path $InstallDirectory 'models'
    $models = @()
    if (Test-Path $modelsDir) { $models = @(Get-ChildItem $modelsDir -Filter '*.onnx' -File -ErrorAction SilentlyContinue) }

    if ($missing.Count -eq 0) {
        $libraryNote = if ($required.Count -eq 0) { 'no extra native libraries needed' }
                       else { "all $($required.Count) native libraries present" }
        Write-Ok "$libraryNote, $($models.Count) model file(s)"
    } else {
        Write-Host "WARNING: AI detection will not run on this node - could not find $($missing.Count) native librar$(if ($missing.Count -eq 1) { 'y' } else { 'ies' }) the CUDA execution provider needs, and they are not installed anywhere this script knows to look:" -ForegroundColor Yellow
        foreach ($item in $missing) {
            Write-Host "    $($item.Dll)  <- $($item.Provides)" -ForegroundColor Yellow
        }
        Write-Host "    Recording and every non-AI detection path are unaffected." -ForegroundColor Yellow
        Write-Host "    Install on THIS machine (not the LarisVMS web server), then re-run this script:" -ForegroundColor Yellow
        Write-Host "      CUDA Toolkit 12.8  https://developer.nvidia.com/cuda-12-8-0-download-archive" -ForegroundColor Yellow
        Write-Host "      cuDNN 9.x          https://developer.nvidia.com/cuda/cuda-x-libraries/cudnn" -ForegroundColor Yellow
        Write-Host "      cuDNN via pip:     pip install --extra-index-url https://pypi.nvidia.com nvidia-cudnn-cu12" -ForegroundColor Yellow
    }

    if ($models.Count -eq 0) {
        Write-Host "WARNING: No .onnx model found in $modelsDir - AI detection has nothing to run." -ForegroundColor Yellow
        Write-Host "    Export one with tools\export-models\ and rebuild the package with build-node.ps1." -ForegroundColor Yellow
    }
}

# Installs an ffmpeg package via winget.exe directly and returns the path ffmpeg.exe landed at.
# Deliberately does NOT go through any PowerShell module layer (Microsoft.WinGet.Client,
# Install-WinGetPackage, etc.): confirmed across attempts on a real recorder machine, that layer is
# unreliable in an Administrator/non-interactive session in ways that differ by machine —
# Repair-WinGetPackageManager missing on one version, Install-Module refusing to clobber
# already-present cmdlets on another, and a third-party "Cobalt" module shadowing the same cmdlet
# names with its own broken winget.exe lookup on this specific one. Locating winget.exe by its own
# well-known install path and invoking it directly sidesteps all of that — no module, no cmdlet
# resolution, just the binary.
function Resolve-WingetExe {
    $onPath = Get-Command winget -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # An Administrator/service-installation session frequently doesn't have the WindowsApps execution
    # alias directory on PATH even though App Installer (and winget.exe itself) is present — this is
    # the same probe dploid's deploy.ps1 uses for exactly that reason.
    $viaWindowsApps = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe" `
        -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ($viaWindowsApps) { return $viaWindowsApps }

    return $null
}

function Install-FfmpegPackage([string]$PackageId) {
    $wingetExe = Resolve-WingetExe
    if (-not $wingetExe) {
        throw "winget.exe could not be located (checked PATH and WindowsApps). Install 'App Installer' " +
              "from the Microsoft Store, or download ffmpeg manually and re-run with -FfmpegPath <path>."
    }

    Write-Host "    Installing $PackageId via $wingetExe..."
    & $wingetExe install --id $PackageId -e --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget install of $PackageId failed (exit $LASTEXITCODE)." }

    # winget installs a portable/zip-style package like this one per-user
    # (%LOCALAPPDATA%\Microsoft\WinGet\Packages\...) regardless of how it was invoked — there is no
    # reliable machine-wide scope for it — so the file has to be located by search rather than
    # assumed on PATH.
    $exe = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter 'ffmpeg.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $exe) { throw "$PackageId installed but ffmpeg.exe could not be located under %LOCALAPPDATA%\Microsoft\WinGet\Packages." }
    return $exe
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

$ffmpegInstallDir = Join-Path $InstallDir 'ffmpeg'
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

# ── resolve ffmpeg ───────────────────────────────────────────────────────────
# Not bundled with the node package — every LAN typically already standardizes on one build/version,
# and downloading it here (rather than checking one into source control) keeps the published node
# package small. The LGPL "shared" build (not a GPL build) is what the plan's licensing note calls
# for; BtbN.FFmpeg.LGPL.Shared on winget matches that.

Write-Step "Resolving ffmpeg"
$alreadyInstalledFfmpeg = Join-Path $ffmpegInstallDir 'ffmpeg.exe'
$resolvedFfmpeg = $null
$reusingInstalledFfmpeg = $false

if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
    if (-not (Test-Path $FfmpegPath)) { throw "Specified -FfmpegPath does not exist: $FfmpegPath" }
    $resolvedFfmpeg = $FfmpegPath
    Write-Ok "Using: $resolvedFfmpeg"
} elseif (Test-Path $alreadyInstalledFfmpeg) {
    # Upgrade path: a previous run of this script already copied ffmpeg into this node's own
    # install dir — reuse it rather than re-resolving from PATH/winget every time this script is
    # re-run just to pick up a new LarisVMS.Node.exe build. Pass -FfmpegPath explicitly to replace it.
    $resolvedFfmpeg = $alreadyInstalledFfmpeg
    $reusingInstalledFfmpeg = $true
    Write-Ok "Reusing already-installed ffmpeg: $resolvedFfmpeg"
} else {
    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath) {
        $resolvedFfmpeg = $onPath.Source
        Write-Ok "Found on PATH: $resolvedFfmpeg"
    } elseif ($InstallFfmpeg) {
        $resolvedFfmpeg = Install-FfmpegPackage -PackageId 'BtbN.FFmpeg.LGPL.Shared'
        Write-Ok "Installed: $resolvedFfmpeg"
    } else {
        throw "ffmpeg not found on PATH. Pass -FfmpegPath <path to ffmpeg.exe>, or -InstallFfmpeg to fetch " +
              "the LGPL shared build (BtbN.FFmpeg.LGPL.Shared) via winget automatically."
    }
}

if ($reusingInstalledFfmpeg) {
    $FfmpegPath = $alreadyInstalledFfmpeg
} else {
    # Copied into the node's own install directory regardless of where it was found — a shared
    # build resolved from PATH, a manually-supplied -FfmpegPath, or a winget install under the
    # *installing user's* %LOCALAPPDATA% are all locations the Windows Service (running as
    # LocalSystem or a dedicated service account, never as whoever happened to run this script) has
    # no guarantee of being able to read. $InstallDir is machine-wide (Program Files), so copying
    # there once, up front, sidesteps that permission gap entirely.
    Write-Step "Copying ffmpeg into $ffmpegInstallDir"
    New-Item -ItemType Directory -Path $ffmpegInstallDir -Force | Out-Null
    # A "shared" ffmpeg build's exe depends on sibling DLLs (avcodec-*.dll etc.) in the same folder —
    # copy everything alongside it, not just ffmpeg.exe. Retried: on an upgrade this overwrites the
    # previous install's files, and Stop-OrphanedFfmpeg above closes most but not necessarily every
    # instance of the handle-release race that can leave one still transiently locked.
    Copy-ItemWithRetry (Join-Path (Split-Path $resolvedFfmpeg -Parent) '*') $ffmpegInstallDir
    $FfmpegPath = Join-Path $ffmpegInstallDir 'ffmpeg.exe'
    if (-not (Test-Path $FfmpegPath)) { throw "Copy completed but ffmpeg.exe is not at the expected path: $FfmpegPath" }
    Write-Ok "ffmpeg ready at $FfmpegPath"
}

# ── install files ─────────────────────────────────────────────────────────────

Write-Step "Installing node to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-ItemWithRetry $BinaryPath (Join-Path $InstallDir 'LarisVMS.Node.exe')
if (Test-Path $UpdaterBinaryPath) {
    Copy-ItemWithRetry $UpdaterBinaryPath (Join-Path $InstallDir 'LarisVMS.NodeUpdater.exe')
}
if ($hasVision) {
    # Every sibling file next to LarisVMS.Vision.Service.exe in the package — its execution
    # provider's own native DLLs (onnxruntime.dll, and for a Cuda build cuDNN/TensorRT's own), not
    # just the exe itself — plus models\. Copied by name pattern rather than an exhaustive fixed
    # list: which native DLLs actually ship alongside it depends on which -Accel variant
    # build-node.ps1 was run with, and hardcoding one variant's set here would silently drop files a
    # different variant needs.
    $visionSourceDir = Split-Path $VisionBinaryPath -Parent
    Copy-ItemWithRetry (Join-Path $visionSourceDir 'LarisVMS.Vision.Service.*') $InstallDir
    Copy-ItemWithRetry (Join-Path $visionSourceDir '*.dll') $InstallDir
    $modelsSourceDir = Join-Path $visionSourceDir 'models'
    if (Test-Path $modelsSourceDir) {
        Copy-ItemWithRetry $modelsSourceDir $InstallDir
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
$argParts = @('--server-url', $ServerUrl, '--registration-key', $RegistrationKey, '--ffmpeg-path', $FfmpegPath, '--live-port', $LivePort)
if ($StorageRoot) { $argParts += @('--storage-root', $StorageRoot) }
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

# ── start ─────────────────────────────────────────────────────────────────────

Write-Step "Starting service '$ServiceName'"
Start-Service -Name $ServiceName
Write-Ok "Started"

Write-Host "`nNode $(if ($isUpgrade) { 'upgraded' } else { 'installed' }) successfully." -ForegroundColor Green
Write-Host "Logs:        Event Viewer > Windows Logs > Application  (source: $ServiceName)"
Write-Host "Install dir: $InstallDir"
Write-Host "Assign cameras to this node from Admin -> Nodes / the camera edit page in LarisVMS.Web."
