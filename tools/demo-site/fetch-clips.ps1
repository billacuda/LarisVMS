<#
.SYNOPSIS
    Downloads each camera's stock clip from Pexels and encodes it into camera-like Main and Sub streams.

.DESCRIPTION
    For every camera in cameras.json:
      <dataRoot>\clips\src\<pexelsId>.mp4   the 1080p download (kept, so re-encoding is offline)
      <dataRoot>\clips\<id>_main.mp4        1920x1080 @ 25 fps, H.264 (or H.265), 2 s GOP, no B-frames
      <dataRoot>\clips\<id>_sub.mp4         640x360 @ 15 fps, H.264, 2 s GOP, no B-frames
    Constant-ish bitrate and fixed GOPs, so the streams behave like a real camera's when looped with
    "-c copy". Cameras with "audio": true get 16 kHz mono AAC on the Main stream.

    Pexels license: https://www.pexels.com/license/ (free to use, no attribution required).

.EXAMPLE
    .\fetch-clips.ps1
    .\fetch-clips.ps1 -Force     # re-encode everything
#>
param([switch]$Force)

. "$PSScriptRoot\common.ps1"

$ffmpeg = Get-ToolPath ffmpeg
$srcDir = Join-Path $ClipsDir 'src'
New-Item -ItemType Directory -Force $srcDir | Out-Null

foreach ($cam in $DemoConfig.cameras) {
    Write-Step "$($cam.id) $($cam.name)"

    $src = Join-Path $srcDir "$($cam.pexelsId).mp4"
    if (-not (Test-Path $src)) {
        # /download/video/<id>/ redirects to the file on videos.pexels.com; w/h pick the 1080p rendition.
        $url = "https://www.pexels.com/download/video/$($cam.pexelsId)/?h=1080&w=1920"
        Write-Host "Downloading $($cam.source)"
        Invoke-WebRequest $url -OutFile $src -UserAgent 'Mozilla/5.0'
    }

    $main = Join-Path $ClipsDir "$($cam.id)_main.mp4"
    $sub  = Join-Path $ClipsDir "$($cam.id)_sub.mp4"

    if ($Force -or -not (Test-Path $main)) {
        $video = if ($cam.codec -eq 'H265') {
            @('-c:v', 'libx265', '-preset', 'medium', '-b:v', '3000k', '-maxrate', '3000k', '-bufsize', '6000k',
              '-x265-params', 'keyint=50:min-keyint=50:scenecut=0:bframes=0:log-level=error', '-tag:v', 'hvc1')
        } else {
            @('-c:v', 'libx264', '-preset', 'medium', '-profile:v', 'main', '-b:v', '4000k', '-maxrate', '4000k', '-bufsize', '8000k',
              '-g', '50', '-keyint_min', '50', '-sc_threshold', '0', '-bf', '0')
        }
        $audio = if ($cam.audio) { @('-c:a', 'aac', '-b:a', '48k', '-ac', '1', '-ar', '16000') } else { @('-an') }
        Invoke-Native $ffmpeg (@('-hide_banner', '-loglevel', 'error', '-y', '-i', $src,
            '-vf', 'fps=25,scale=1920:1080:flags=lanczos,format=yuv420p') + $video + $audio + @('-movflags', '+faststart', $main))
        Write-Ok "  $main"
    }

    if ($Force -or -not (Test-Path $sub)) {
        Invoke-Native $ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-i', $src,
            '-vf', 'fps=15,scale=640:360:flags=lanczos,format=yuv420p',
            '-c:v', 'libx264', '-preset', 'medium', '-profile:v', 'main', '-b:v', '512k', '-maxrate', '512k', '-bufsize', '1024k',
            '-g', '30', '-keyint_min', '30', '-sc_threshold', '0', '-bf', '0', '-an', '-movflags', '+faststart', $sub)
        Write-Ok "  $sub"
    }
}

Write-Ok "`nClips ready in $ClipsDir"
