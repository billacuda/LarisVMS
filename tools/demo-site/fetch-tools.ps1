<#
.SYNOPSIS
    Downloads MediaMTX (the RTSP server the fake cameras stream from) into tools\demo-site\bin\, and
    unpacks the LarisVMS recorder node (with its Vision Service) from the release MSI into <dataRoot>.

.DESCRIPTION
    MediaMTX is a single MIT-licensed executable: https://github.com/bluenviron/mediamtx
    bin\ is gitignored. ffmpeg must already be installed (winget install ffmpeg --scope machine).
#>
param(
    [string]$Version = 'v1.21.1',
    [string]$NodeRelease = 'v0.221.0'
)

. "$PSScriptRoot\common.ps1"

# The default MediaMTX version's zip, pinned so a swapped download is refused. Another -Version is
# checked against that release's own checksums.sha256.
$PinnedMediaMtx = @{ 'v1.21.1' = 'faa97974861eb75a68b5aa326c78e7e7a6f670b5ef191bace78e715130381f23' }

function Assert-Sha256([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        Remove-Item $Path -ErrorAction SilentlyContinue
        throw "$(Split-Path $Path -Leaf) isn't the expected file (SHA-256 $actual, expected $Expected). It was deleted."
    }
}

# The SHA-256 a release's checksum file lists for $FileName, or $null when there's no such file.
function Get-ReleaseSha256([string]$SumsUrl, [string]$FileName) {
    try { $text = (Invoke-WebRequest $SumsUrl -UseBasicParsing).Content } catch { return $null }
    if ($text -is [byte[]]) { $text = [Text.Encoding]::ASCII.GetString($text) }
    foreach ($line in $text -split "`n") {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $FileName) { return $Matches[1] }
    }
    return $null
}

New-Item -ItemType Directory -Force $BinDir | Out-Null
$exe = Join-Path $BinDir 'mediamtx.exe'
if (Test-Path $exe) { Write-Ok "MediaMTX already present: $exe" }
else {
    Write-Step "Downloading MediaMTX $Version"
    $zip = Join-Path $BinDir 'mediamtx.zip'
    $zipName = "mediamtx_${Version}_windows_amd64.zip"
    Invoke-WebRequest "https://github.com/bluenviron/mediamtx/releases/download/$Version/$zipName" -OutFile $zip
    $expected = $PinnedMediaMtx[$Version]
    if (-not $expected) { $expected = Get-ReleaseSha256 "https://github.com/bluenviron/mediamtx/releases/download/$Version/checksums.sha256" $zipName }
    if (-not $expected) { Remove-Item $zip; throw "No checksum found for $zipName, so it wasn't unpacked." }
    Assert-Sha256 $zip $expected
    Expand-Archive $zip -DestinationPath $BinDir -Force
    Remove-Item $zip
    # The release's sample config isn't used; start-demo.ps1 writes its own.
    Remove-Item (Join-Path $BinDir 'mediamtx.yml') -ErrorAction SilentlyContinue
    Write-Ok "Installed $exe"
}

# The recorder node comes from the release MSI rather than a source build, because the Vision
# Service (AI detection) needs src\LarisVMS.Vision\Models\, which the repo's ".gitignore: models/"
# rule keeps out of git. "msiexec /a" only unpacks the files; nothing is installed.
$nodeExe = Join-Path $DataRoot "node-msi\PFiles64\LarisVMS\Node\LarisVMS.Node.exe"
if (Test-Path $nodeExe) { Write-Ok "Node already extracted: $nodeExe" }
else {
    Write-Step "Downloading the LarisVMS $NodeRelease node installer"
    $msiDir = Join-Path $DataRoot 'msi'
    New-Item -ItemType Directory -Force $msiDir | Out-Null
    $version = $NodeRelease -replace '^v', '' -replace '-.*$', ''
    $msi = Join-Path $msiDir "LarisVMS-Node-$version-x64.msi"
    if (-not (Test-Path $msi)) {
        Invoke-WebRequest "https://github.com/billacuda/LarisVMS/releases/download/$NodeRelease/LarisVMS-Node-$version-x64.msi" -OutFile $msi
    }
    # Releases from 0.220 on carry SHA256SUMS.txt (build-installers.ps1); older ones only warn.
    $expected = Get-ReleaseSha256 "https://github.com/billacuda/LarisVMS/releases/download/$NodeRelease/SHA256SUMS.txt" (Split-Path $msi -Leaf)
    if ($expected) { Assert-Sha256 $msi $expected }
    else { Write-Warning "Release $NodeRelease has no SHA256SUMS.txt, so $(Split-Path $msi -Leaf) wasn't checked." }
    $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$(Join-Path $DataRoot 'node-msi')`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Extracting $msi failed (msiexec exit $($p.ExitCode))." }
    Write-Ok "Extracted $nodeExe"
}

Get-ToolPath ffmpeg | Out-Null
Write-Ok 'ffmpeg found.'
