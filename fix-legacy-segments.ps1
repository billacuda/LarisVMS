<#
.SYNOPSIS
    Remuxes recorded segments written before the 0.21.1 recorder fix so they play back in a browser.

.DESCRIPTION
    Segments recorded before 0.21.1 were written without the mov muxer's default_base_moof flag (it
    was set only on the live-view leg of the recorder's ffmpeg tee). Without it, a fragment's sample
    offsets are file-relative rather than relative to its own moof, which the MSE byte-stream format
    does not accept: Chrome takes the append, fires updateend normally, and produces no buffered
    range at all. The files are perfectly valid MP4 otherwise and play fine in VLC or ffplay — only
    browser playback is affected.

    This rewrites the container in place with -c copy. No decode, no re-encode, no quality change and
    no change to the video or audio bitstream; it runs at roughly disk speed.

    Safe by default:
      * Dry run unless -Apply is passed.
      * Files that already carry the flag (anything recorded after the fix) are detected and skipped,
        so the script is idempotent and safe to re-run.
      * The segment currently being written is skipped — both by a recency window and by a write-lock
        check — so it can run against a live recorder without stopping the node service.
      * The remuxed file is verified to actually carry the flag before it replaces anything. If the
        remux or the verification fails, the original is left untouched.
      * Original file timestamps are preserved. This matters: segment start/end times are derived
        from file metadata (see RecordingSegment), not from the filename, so clobbering them would
        corrupt the timeline for that footage.

    Segment file paths in the database are unchanged, since each file is rewritten in place.

.PARAMETER StorageRoot
    Root directory holding recordings. Defaults to the node's own default location. If the node was
    configured with a different storage root (server-side config or --storage-root), pass it here.

.PARAMETER FfmpegPath
    ffmpeg executable. Leave unset to resolve one automatically (see -InstallFfmpeg). Only needed
    with -Apply; a dry run does not use ffmpeg at all.

.PARAMETER InstallFfmpeg
    If no ffmpeg can be found, download the LGPL shared build and use it. Downloads the zip straight
    from BtbN's GitHub releases rather than going through winget — winget is frequently unavailable
    on a file server (App Installer is not present on Windows Server by default and the Store isn't
    an option there), and it is only ever fetching this same zip anyway.

.PARAMETER FfmpegInstallDir
    Where -InstallFfmpeg puts ffmpeg. A previous download here is reused instead of re-fetching.

.PARAMETER Apply
    Actually rewrite files. Without this the script only reports what it would do.

.PARAMETER SkipRecentMinutes
    Leave files modified within this many minutes alone — the recorder is probably still writing
    them. Default 5.

.EXAMPLE
    .\fix-legacy-segments.ps1 -StorageRoot \\files1\recordings
    Dry run against a share: reports how many segments need fixing. Needs no ffmpeg.

.EXAMPLE
    .\fix-legacy-segments.ps1 -StorageRoot \\files1\recordings -Apply
    Run from a recorder node against a share. Finds the node's own bundled ffmpeg automatically,
    so nothing needs installing on the file server itself.

.EXAMPLE
    .\fix-legacy-segments.ps1 -StorageRoot D:\Recordings -Apply -InstallFfmpeg
    Run directly on a machine with no ffmpeg: downloads one, then fixes everything under D:\Recordings.

.NOTES
    This does not have to run on the file server. It only needs write access to the recordings, so
    running it from a recorder node against a UNC path is usually the least-setup option — the node
    already has a suitable ffmpeg bundled with it.
#>
[CmdletBinding()]
param(
    [string]$StorageRoot = (Join-Path $env:ProgramData 'LarisVMS\recordings'),
    [string]$FfmpegPath = '',
    [switch]$InstallFfmpeg,
    [string]$FfmpegInstallDir = (Join-Path $env:LOCALAPPDATA 'LarisVMS\ffmpeg'),
    [switch]$Apply,
    [int]$SkipRecentMinutes = 5
)

$ErrorActionPreference = 'Stop'

# The mov muxer flags the recorder now writes. Kept identical to RecordingSession.MseMovFlags —
# a remux that produced anything different would just move the incompatibility somewhere else.
$MseMovFlags = '+frag_keyframe+empty_moov+default_base_moof'

# tfhd flag bit for default-base-is-moof, the one MSE requires.
$DefaultBaseIsMoof = 0x020000

function Read-U32BE([System.IO.Stream]$stream) {
    $b = [byte[]]::new(4)
    if ($stream.Read($b, 0, 4) -ne 4) { return $null }
    return ([uint32]$b[0] -shl 24) -bor ([uint32]$b[1] -shl 16) -bor ([uint32]$b[2] -shl 8) -bor [uint32]$b[3]
}

# Walks the ISO-BMFF box tree looking for the first moof/traf/tfhd and returns whether
# default-base-is-moof is set on it. Seeks box-to-box rather than reading whole files — a segment is
# several MB and only its first fragment header is needed to decide.
function Test-HasDefaultBaseIsMoof([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        return (Find-TfhdFlag -Stream $fs -RegionEnd $fs.Length)
    }
    finally { $fs.Dispose() }
}

function Find-TfhdFlag([System.IO.Stream]$Stream, [long]$RegionEnd) {
    # Containers worth descending into on the way to a tfhd. Everything else is skipped wholesale.
    $containers = @('moof', 'traf')

    while ($Stream.Position + 8 -le $RegionEnd) {
        $boxStart = $Stream.Position
        $size = Read-U32BE $Stream
        if ($null -eq $size) { return $null }

        $typeBytes = [byte[]]::new(4)
        if ($Stream.Read($typeBytes, 0, 4) -ne 4) { return $null }
        $type = [System.Text.Encoding]::ASCII.GetString($typeBytes)

        $headerSize = 8
        [long]$boxSize = $size
        if ($size -eq 1) {
            # 64-bit largesize follows the type field.
            $hi = Read-U32BE $Stream; $lo = Read-U32BE $Stream
            if ($null -eq $hi -or $null -eq $lo) { return $null }
            $boxSize = ([long]$hi -shl 32) -bor [long]$lo
            $headerSize = 16
        }
        elseif ($size -eq 0) {
            # Box extends to end of file.
            $boxSize = $RegionEnd - $boxStart
        }

        if ($boxSize -lt $headerSize) { return $null }   # malformed; give up rather than loop forever
        if ($boxStart + $boxSize -gt $RegionEnd) { return $null }

        if ($type -eq 'tfhd') {
            # FullBox: 1 byte version then 3 bytes of flags.
            $vf = [byte[]]::new(4)
            if ($Stream.Read($vf, 0, 4) -ne 4) { return $null }
            $flags = ([int]$vf[1] -shl 16) -bor ([int]$vf[2] -shl 8) -bor [int]$vf[3]
            return (($flags -band $DefaultBaseIsMoof) -ne 0)
        }

        if ($containers -contains $type) {
            $result = Find-TfhdFlag -Stream $Stream -RegionEnd ($boxStart + $boxSize)
            if ($null -ne $result) { return $result }
        }

        $Stream.Position = $boxStart + $boxSize
    }
    return $null
}

# BtbN's LGPL shared build — the same package install-node.ps1 asks winget for
# (BtbN.FFmpeg.LGPL.Shared), fetched directly instead. LGPL rather than GPL for the same licensing
# reason the node install uses it. "latest" is a rolling release tag, so this URL stays current.
$FfmpegDownloadUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip'

function Install-Ffmpeg([string]$DestinationDir) {
    $existing = Get-ChildItem $DestinationDir -Filter 'ffmpeg.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($existing) {
        Write-Host "    Reusing previously downloaded ffmpeg: $existing"
        return $existing
    }

    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null
    $zip = Join-Path $DestinationDir 'ffmpeg.zip'

    Write-Host "    Downloading ffmpeg (~65 MB) from $FfmpegDownloadUrl"
    # Invoke-WebRequest's progress bar makes a download of this size dramatically slower than the
    # transfer itself warrants, so it's suppressed for the duration.
    $prev = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $FfmpegDownloadUrl -OutFile $zip -MaximumRedirection 5 }
    finally { $ProgressPreference = $prev }

    Write-Host '    Extracting...'
    Expand-Archive -LiteralPath $zip -DestinationPath $DestinationDir -Force
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    # The zip nests everything under a versioned folder, so search rather than assume a path.
    $exe = Get-ChildItem $DestinationDir -Filter 'ffmpeg.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $exe) { throw "Downloaded ffmpeg but could not find ffmpeg.exe under $DestinationDir." }
    return $exe
}

# Resolution order: an explicit path, then the recorder node's own bundled copy (which is why this
# usually needs no setup at all when run from a node), then PATH, then a download if allowed. This
# is a shared build, so ffmpeg.exe is always used where it sits — its sibling avcodec-*.dll etc.
# have to stay alongside it.
function Resolve-Ffmpeg {
    if (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) {
        if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw "Specified -FfmpegPath does not exist: $FfmpegPath" }
        return $FfmpegPath
    }

    $bundled = 'C:\Program Files\LarisVMS\Node\ffmpeg\ffmpeg.exe'
    if (Test-Path -LiteralPath $bundled) { return $bundled }

    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    if ($InstallFfmpeg) { return (Install-Ffmpeg -DestinationDir $FfmpegInstallDir) }

    throw @"
No ffmpeg found. Any one of these fixes it:
  * Run this from a recorder node instead — -StorageRoot accepts a UNC path, and the node already
    has a suitable ffmpeg bundled, so nothing needs installing on the file server.
  * Pass -FfmpegPath <path to ffmpeg.exe>.
  * Pass -InstallFfmpeg to download the LGPL shared build automatically (no winget required).
"@
}

# A file the recorder still has open for writing must not be touched. The recency window catches the
# common case cheaply; this catches the rest (a stalled or slow-rotating stream).
function Test-IsWritable([string]$Path) {
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $fs.Dispose()
        return $true
    }
    catch { return $false }
}

# ── preflight ────────────────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $StorageRoot)) {
    throw "Storage root not found: $StorageRoot`nPass -StorageRoot with the path this node actually records to."
}
# Only -Apply needs ffmpeg — a dry run just reads box headers, so it stays useful on a machine that
# has no ffmpeg and no way to install one.
$resolvedFfmpeg = $null
if ($Apply) {
    Write-Host 'Resolving ffmpeg...'
    $resolvedFfmpeg = Resolve-Ffmpeg
    # Confirm it actually launches rather than discovering it's broken partway through a run.
    & $resolvedFfmpeg -version 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg found at $resolvedFfmpeg but it failed to run (exit $LASTEXITCODE)." }
}

Write-Host "Storage root : $StorageRoot"
Write-Host "ffmpeg       : $(if ($resolvedFfmpeg) { $resolvedFfmpeg } else { '(not needed for a dry run)' })"
Write-Host "Mode         : $(if ($Apply) { 'APPLY — files will be rewritten' } else { 'DRY RUN — nothing will be modified' })"
Write-Host ''

$cutoff = (Get-Date).AddMinutes(-$SkipRecentMinutes)
$files = Get-ChildItem -LiteralPath $StorageRoot -Recurse -File -Filter '*.mp4' -ErrorAction SilentlyContinue

$stats = [ordered]@{
    Scanned      = 0
    AlreadyOk    = 0
    NeedsFix     = 0
    Fixed        = 0
    SkippedRecent= 0
    SkippedLocked= 0
    Unreadable   = 0
    Failed       = 0
}

foreach ($file in $files) {
    $stats.Scanned++

    if ($file.LastWriteTime -gt $cutoff) { $stats.SkippedRecent++; continue }

    try { $hasFlag = Test-HasDefaultBaseIsMoof -Path $file.FullName }
    catch { Write-Warning "Could not read $($file.FullName): $($_.Exception.Message)"; $stats.Unreadable++; continue }

    if ($null -eq $hasFlag) { Write-Warning "No fragment header found in $($file.FullName) — skipping."; $stats.Unreadable++; continue }
    if ($hasFlag) { $stats.AlreadyOk++; continue }

    $stats.NeedsFix++

    if (-not $Apply) {
        Write-Host "would fix : $($file.FullName)"
        continue
    }

    if (-not (Test-IsWritable $file.FullName)) {
        Write-Warning "In use, skipping: $($file.FullName)"
        $stats.SkippedLocked++
        continue
    }

    # Same directory as the original so the final move stays on one volume. The .tmp extension is
    # deliberate: the recorder indexes *.mp4 in its output tree, and a temp file it could pick up
    # mid-remux would be indexed as a real segment. It does mean ffmpeg can't infer the output
    # format from the extension, hence the explicit -f mp4 — without it ffmpeg fails outright with
    # "Unable to choose an output format".
    $tmp = "$($file.FullName).remux.tmp"
    try {
        $ffOutput = & $resolvedFfmpeg -nostdin -v error -y -i $file.FullName -c copy -movflags $MseMovFlags -f mp4 $tmp 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmp)) {
            $detail = if ($ffOutput) { ($ffOutput | Select-Object -Last 3) -join ' | ' } else { '(no output)' }
            throw "ffmpeg exited with code $LASTEXITCODE - $detail"
        }

        # Never trust the remux blindly — a file that came out without the flag would replace a
        # working-in-VLC original with something no better, and re-running wouldn't notice.
        if ((Test-HasDefaultBaseIsMoof -Path $tmp) -ne $true) {
            throw 'remuxed file still lacks default-base-is-moof'
        }
        if ((Get-Item -LiteralPath $tmp).Length -le 0) {
            throw 'remuxed file is empty'
        }

        # Segment start/end times are derived from file metadata, so these carry real meaning.
        $created = $file.CreationTime
        $written = $file.LastWriteTime

        Move-Item -LiteralPath $tmp -Destination $file.FullName -Force

        $rewritten = Get-Item -LiteralPath $file.FullName
        $rewritten.CreationTime = $created
        $rewritten.LastWriteTime = $written

        $stats.Fixed++
        Write-Host "fixed : $($file.FullName)"
    }
    catch {
        Write-Warning "FAILED $($file.FullName): $($_.Exception.Message)"
        $stats.Failed++
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
Write-Host '── Summary ──'
foreach ($k in $stats.Keys) { '{0,-14}: {1}' -f $k, $stats[$k] | Write-Host }

if (-not $Apply -and $stats.NeedsFix -gt 0) {
    Write-Host ''
    Write-Host "Re-run with -Apply to rewrite these $($stats.NeedsFix) file(s)." -ForegroundColor Yellow
}
if ($stats.Failed -gt 0) {
    Write-Host ''
    Write-Host "$($stats.Failed) file(s) failed and were left untouched." -ForegroundColor Yellow
}
