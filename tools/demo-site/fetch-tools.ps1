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
    [string]$NodeRelease = 'v0.209.0-beta'
)

. "$PSScriptRoot\common.ps1"

New-Item -ItemType Directory -Force $BinDir | Out-Null
$exe = Join-Path $BinDir 'mediamtx.exe'
if (Test-Path $exe) { Write-Ok "MediaMTX already present: $exe" }
else {
    Write-Step "Downloading MediaMTX $Version"
    $zip = Join-Path $BinDir 'mediamtx.zip'
    Invoke-WebRequest "https://github.com/bluenviron/mediamtx/releases/download/$Version/mediamtx_${Version}_windows_amd64.zip" -OutFile $zip
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
    $p = Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$(Join-Path $DataRoot 'node-msi')`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Extracting $msi failed (msiexec exit $($p.ExitCode))." }
    Write-Ok "Extracted $nodeExe"
}

Get-ToolPath ffmpeg | Out-Null
Write-Ok 'ffmpeg found.'
