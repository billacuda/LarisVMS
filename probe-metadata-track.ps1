<#
.SYNOPSIS
    Research spike: does a camera actually expose an ONVIF metadata track over RTSP, and can ffmpeg
    read it? Answers the one open question blocking bounding-box overlays.

.DESCRIPTION
    Bounding boxes need per-frame object coordinates, which travel on a separate ONVIF metadata
    stream (application/vnd.onvif.metadata) — not on the rule-engine event topics LarisVMS already
    consumes (those report *that* a person was seen, not where). Before any of that can be designed,
    two things have to be true, and neither can be assumed:

      1. The camera actually multiplexes a data stream onto the same RTSP session ffmpeg opens.
         Plenty of cameras advertise MetadataConfiguration over ONVIF and still never do this.
      2. ffmpeg can demux that stream, and what comes out is really ONVIF MetadataStream XML.

    Point 2 matters especially here: this codebase has a confirmed production failure on record from
    mapping a camera's data stream (`-map 0`), where the MP4 muxer aborted the *entire* recording tee
    — video and audio included — because it had no tag for that stream. That is exactly why the
    recording pipeline maps only `0:v` and `0:a?` today.

    This script only ever reads. It opens its own short-lived connection, entirely separate from the
    recording pipeline, and touches nothing the recorders are doing.

.PARAMETER RtspUri
    The camera's RTSP URL. Credentials can be embedded (rtsp://user:pass@host/...) or supplied via
    -Credential instead, which keeps them out of your shell history.

.PARAMETER Credential
    Camera username/password, if not embedded in the URL.

.PARAMETER FfmpegPath
    Defaults to whatever "ffmpeg"/"ffprobe" resolve to on PATH.

.EXAMPLE
    .\probe-metadata-track.ps1 -RtspUri "rtsp://192.168.1.50:554/cam/realmonitor?channel=1&subtype=0" -Credential (Get-Credential)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RtspUri,
    [System.Management.Automation.PSCredential]$Credential,
    [string]$FfmpegPath = "ffmpeg",
    [string]$FfprobePath = "ffprobe"
)

$ErrorActionPreference = 'Stop'

# Credentials are injected into the URL only in memory, and every message this script prints uses
# the redacted form — a camera password must not end up in a console transcript or a pasted log.
$uri = $RtspUri
if ($Credential) {
    $parsed = [Uri]$RtspUri
    $user = [Uri]::EscapeDataString($Credential.UserName)
    $pass = [Uri]::EscapeDataString($Credential.GetNetworkCredential().Password)
    $uri = "{0}://{1}:{2}@{3}{4}{5}" -f $parsed.Scheme, $user, $pass, $parsed.Host,
        $(if ($parsed.IsDefaultPort) { "" } else { ":$($parsed.Port)" }), $parsed.PathAndQuery
}
$redacted = [Regex]::Replace($uri, '://[^@/]+@', '://***:***@')

Write-Host "=== Step 1: what streams does this camera actually expose? ===" -ForegroundColor Cyan
Write-Host "Probing $redacted" -ForegroundColor DarkGray

$probeArgs = @(
    '-v', 'error',
    '-rtsp_transport', 'tcp',
    '-show_entries', 'stream=index,codec_type,codec_name,codec_tag_string',
    '-of', 'json',
    '-i', $uri
)
$probeJson = & $FfprobePath @probeArgs 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    Write-Host "ffprobe failed — check the URL/credentials and that this machine can reach the camera." -ForegroundColor Red
    Write-Host $probeJson
    exit 1
}

Write-Host $probeJson
$streams = ($probeJson | ConvertFrom-Json).streams
$dataStreams = @($streams | Where-Object { $_.codec_type -eq 'data' })

if ($dataStreams.Count -eq 0) {
    Write-Host "RESULT: no data stream on this RTSP session." -ForegroundColor Yellow
    Write-Host "Bounding boxes are not reachable this way on this camera, regardless of what its" -ForegroundColor Yellow
    Write-Host "ONVIF capabilities advertise. Object-detection *events* (already shipped in 0.85.0)" -ForegroundColor Yellow
    Write-Host "remain the available signal. Worth re-running against other camera models before" -ForegroundColor Yellow
    Write-Host "concluding anything fleet-wide." -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "RESULT: found $($dataStreams.Count) data stream(s)." -ForegroundColor Green
$dataStreams | ForEach-Object {
    Write-Host ("  index {0}  codec={1}  tag={2}" -f $_.index, $_.codec_name, $_.codec_tag_string) -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Step 2: can ffmpeg demux it, and is it ONVIF XML? ===" -ForegroundColor Cyan
Write-Host "Capturing ~15s of the data stream in isolation (nothing near the recording pipeline)." -ForegroundColor DarkGray

$outFile = Join-Path ([IO.Path]::GetTempPath()) "larisvms-metadata-probe.bin"
if (Test-Path $outFile) { Remove-Item $outFile -Force }

# -map 0:d alone, to its own file. Deliberately NOT the tee/MP4 path that previously aborted a whole
# recording — the point is to learn whether the demux works at all, in a place where failing is free.
$captureArgs = @(
    '-nostdin', '-v', 'warning',
    '-rtsp_transport', 'tcp',
    '-t', '15',
    '-i', $uri,
    '-map', '0:d',
    '-c', 'copy',
    '-f', 'data',
    '-y', $outFile
)
& $FfmpegPath @captureArgs 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

if (-not (Test-Path $outFile) -or (Get-Item $outFile).Length -eq 0) {
    Write-Host "RESULT: the stream exists but produced no demuxable bytes in 15s." -ForegroundColor Yellow
    Write-Host "Either nothing was happening in frame (metadata is often only emitted on activity —" -ForegroundColor Yellow
    Write-Host "worth retrying while walking in front of the camera), or ffmpeg can't read this" -ForegroundColor Yellow
    Write-Host "particular stream. Not yet a definitive no." -ForegroundColor Yellow
    exit 0
}

$size = (Get-Item $outFile).Length
Write-Host ""
Write-Host "RESULT: captured $size bytes to $outFile" -ForegroundColor Green
Write-Host "First 600 bytes — ONVIF metadata should look like XML with a tt:MetadataStream root:" -ForegroundColor Cyan

$bytes = [IO.File]::ReadAllBytes($outFile)
$preview = [Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min(600, $bytes.Length))
Write-Host $preview

Write-Host ""

# Checked against the WHOLE capture, not the printed preview, and specifically for the elements that
# actually carry object geometry. An earlier version of this check accepted a match on
# "MetadataStream|VideoAnalytics" and reported bounding boxes as feasible — which was a false
# positive against a real camera whose metadata track carries only a cell-motion grid. Those two
# elements are present in *any* ONVIF metadata stream and say nothing about object detection.
$full = [Text.Encoding]::UTF8.GetString($bytes)
$hasObjects = $full -match '<tt:Object\b' -or $full -match 'BoundingBox'
$hasClasses = $full -match 'ClassCandidate|<tt:Class\b'
$hasMotionCells = $full -match 'MotionInCells'
$isOnvifXml = $full -match 'MetadataStream'

if (-not $isOnvifXml) {
    Write-Host "Bytes came through but this isn't ONVIF MetadataStream XML — inspect" -ForegroundColor Yellow
    Write-Host "$outFile before drawing conclusions." -ForegroundColor Yellow
    exit 0
}

if ($hasObjects) {
    Write-Host "RESULT: object geometry IS present — bounding boxes are reachable on this camera." -ForegroundColor Green
    if ($hasClasses) {
        Write-Host "Object class labels are present too, so boxes could be labelled per object." -ForegroundColor Green
    } else {
        Write-Host "No class labels though (no ClassCandidate/tt:Class), so boxes would be unlabelled" -ForegroundColor Yellow
        Write-Host "geometry unless the class comes from the rule-engine events instead." -ForegroundColor Yellow
    }
    Write-Host "Next step is designing how to consume the track (a third tee leg vs. a separate" -ForegroundColor Green
    Write-Host "ffmpeg process), keeping the recording pipeline's own mapping untouched." -ForegroundColor Green
} else {
    Write-Host "RESULT: valid ONVIF metadata, but NO object geometry in this capture." -ForegroundColor Yellow
    if ($hasMotionCells) {
        Write-Host "What it does carry is a MotionInCells grid — coarse per-cell motion, not object" -ForegroundColor Yellow
        Write-Host "rectangles. That is a motion heatmap, and is not enough for bounding boxes." -ForegroundColor Yellow
    }
    Write-Host "Before concluding: metadata is usually only emitted during activity, so re-run this" -ForegroundColor Yellow
    Write-Host "with someone walking through frame. If object elements still never appear, this" -ForegroundColor Yellow
    Write-Host "camera model does not do onboard object detection (or it is disabled in its own" -ForegroundColor Yellow
    Write-Host "web UI), and bounding boxes are not achievable from it at any amount of effort here." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Element census across the whole capture (what the camera actually sent):" -ForegroundColor Cyan
[Regex]::Matches($full, '<(tt:[A-Za-z]+)') |
    ForEach-Object { $_.Groups[1].Value } |
    Group-Object |
    Sort-Object Count -Descending |
    ForEach-Object { Write-Host ("  {0,-4} {1}" -f $_.Count, $_.Name) -ForegroundColor DarkGray }
