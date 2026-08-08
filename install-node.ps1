<#
.SYNOPSIS
    Install a recorder node (Rcordr.Node) as a Windows Service.

.DESCRIPTION
    Installs the pre-built Rcordr.Node.exe (expected in the same folder as this script — see
    build-node.ps1) to $InstallDir, registers it as a Windows Service with the registration
    arguments baked into the service's command line, configures automatic restart on failure, and
    starts it.

    On first start the service registers itself with Rcordr.Web using -RegistrationKey (from
    Admin -> Nodes, or the Setup wizard's Node step) and persists the assigned NodeId/secret to
    %ProgramData%\Rcordr\node.config (DPAPI-protected, LocalMachine scope). Every subsequent start
    — including a re-run of this script to change settings — loads that file and skips
    re-registration, so re-running this script is safe.

    Storage access: if the storage root is a network share (\\server\share\...), the service must
    run as an account with access to it — the default LocalSystem account generally cannot
    authenticate to SMB shares. Pass -ServiceCredential for a domain/service account in that case.

.EXAMPLE
    .\install-node.ps1 -ServerUrl "https://rcordr.example.com" -RegistrationKey "abc123"
    .\install-node.ps1 -ServerUrl "https://rcordr.example.com" -RegistrationKey "abc123" -InsecureTls
    .\install-node.ps1 -ServerUrl "https://rcordr.example.com" -RegistrationKey "abc123" -ServiceCredential (Get-Credential)
#>

param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$RegistrationKey,
    [string]$BinaryPath        = (Join-Path $PSScriptRoot 'Rcordr.Node.exe'),
    [string]$InstallDir        = 'C:\Program Files\Rcordr\Node',
    [string]$ServiceName       = 'RcordrNode',
    [string]$ServiceDisplay    = 'Rcordr Node',
    [string]$FfmpegPath        = '',
    [switch]$InstallFfmpeg,
    [string]$StorageRoot       = '',
    [switch]$InsecureTls,
    [pscredential]$ServiceCredential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }

function Format-ServiceArg([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
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
    throw "Binary not found: $BinaryPath`nBuild it first with: .\build-node.ps1 (run on a dev machine, then copy the publish\Rcordr.Node\win\ folder here)."
}

# ── stop + remove existing service ───────────────────────────────────────────
# Must happen before anything below touches $InstallDir: a running node's ffmpeg.exe/DLLs and its
# own Rcordr.Node.exe can be locked by the currently-running process, so overwriting them while the
# old service is still up (the normal case for an in-place upgrade of a live recorder) can fail
# mid-copy. Stopping first — even on a fresh install where $existingSvc is null and this is a no-op —
# guarantees every copy below lands on an unlocked target.

$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Step "Stopping existing service '$ServiceName'"
    if ($existingSvc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existingSvc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Write-Ok "Stopped"
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Ok "Removed (node.config and any recordings already on disk are untouched)"
}

# ── resolve ffmpeg ───────────────────────────────────────────────────────────
# Not bundled with the node package — every LAN typically already standardizes on one build/version,
# and downloading it here (rather than checking one into source control) keeps the published node
# package small. The LGPL "shared" build (not a GPL build) is what the plan's licensing note calls
# for; BtbN.FFmpeg.LGPL.Shared on winget matches that.

Write-Step "Resolving ffmpeg"
$resolvedFfmpeg = $null
if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
    if (-not (Test-Path $FfmpegPath)) { throw "Specified -FfmpegPath does not exist: $FfmpegPath" }
    $resolvedFfmpeg = $FfmpegPath
    Write-Ok "Using: $resolvedFfmpeg"
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

# Copied into the node's own install directory regardless of where it was found — a shared build
# resolved from PATH, a manually-supplied -FfmpegPath, or a winget install under the *installing
# user's* %LOCALAPPDATA% are all locations the Windows Service (running as LocalSystem or a
# dedicated service account, never as whoever happened to run this script) has no guarantee of
# being able to read. $InstallDir is machine-wide (Program Files) and was just created below, so
# copying there once, up front, sidesteps that permission gap entirely rather than depending on the
# service account's access to wherever ffmpeg happened to be resolved from.
$ffmpegInstallDir = Join-Path $InstallDir 'ffmpeg'
Write-Step "Copying ffmpeg into $ffmpegInstallDir"
New-Item -ItemType Directory -Path $ffmpegInstallDir -Force | Out-Null
# A "shared" ffmpeg build's exe depends on sibling DLLs (avcodec-*.dll etc.) in the same folder —
# copy everything alongside it, not just ffmpeg.exe.
Copy-Item (Join-Path (Split-Path $resolvedFfmpeg -Parent) '*') $ffmpegInstallDir -Recurse -Force
$FfmpegPath = Join-Path $ffmpegInstallDir 'ffmpeg.exe'
if (-not (Test-Path $FfmpegPath)) { throw "Copy completed but ffmpeg.exe is not at the expected path: $FfmpegPath" }
Write-Ok "ffmpeg ready at $FfmpegPath"

# ── install files ─────────────────────────────────────────────────────────────

Write-Step "Installing node to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $BinaryPath (Join-Path $InstallDir 'Rcordr.Node.exe') -Force
Write-Ok "Files installed"

# ── register service ──────────────────────────────────────────────────────────
# Registration/heartbeat arguments are baked into the service's own command line rather than a
# config file: Program.cs already accepts them as CLI args (and RCORDR_* environment variables) for
# interactive testing, so reusing that same surface here needs no extra code path. They're only
# actually used on the very first start — once node.config exists, Program.cs skips registration
# and just loads the persisted NodeId/secret, so re-running this script to rotate, say, the ffmpeg
# path is safe and won't re-register a second node.

Write-Step "Registering Windows Service '$ServiceName'"

$exePath = Join-Path $InstallDir 'Rcordr.Node.exe'
$argParts = @('--server-url', $ServerUrl, '--registration-key', $RegistrationKey, '--ffmpeg-path', $FfmpegPath)
if ($StorageRoot) { $argParts += @('--storage-root', $StorageRoot) }
if ($InsecureTls) { $argParts += '--insecure-tls' }

$binPath = (Format-ServiceArg $exePath) + ' ' + (($argParts | ForEach-Object { Format-ServiceArg $_ }) -join ' ')

$serviceParams = @{
    Name            = $ServiceName
    DisplayName     = $ServiceDisplay
    Description     = 'Rcordr recorder node - supervises FFmpeg-based 24/7 camera recording.'
    BinaryPathName  = $binPath
    StartupType     = 'Automatic'
}
if ($ServiceCredential) {
    $serviceParams['Credential'] = $ServiceCredential
    Write-Host "    Running as $($ServiceCredential.UserName) (needed for network/SMB storage access)."
} else {
    Write-Host "    Running as LocalSystem - this CANNOT access a network (\\server\share) storage root." -ForegroundColor Yellow
    Write-Host "    Re-run with -ServiceCredential (Get-Credential) if the storage root is a network share." -ForegroundColor Yellow
}

New-Service @serviceParams | Out-Null
Write-Ok "Registered"

# ── configure service recovery ────────────────────────────────────────────────

Write-Step "Configuring service recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Write-Ok "Recovery configured (restart on 1st/2nd/3rd failure; reset after 1 day)"

# ── start ─────────────────────────────────────────────────────────────────────

Write-Step "Starting service '$ServiceName'"
Start-Service -Name $ServiceName
Write-Ok "Started"

Write-Host "`nNode installed successfully." -ForegroundColor Green
Write-Host "Logs:        Event Viewer > Windows Logs > Application  (source: $ServiceName)"
Write-Host "Install dir: $InstallDir"
Write-Host "Assign cameras to this node from Admin -> Nodes / the camera edit page in Rcordr.Web."
