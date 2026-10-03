<#
.SYNOPSIS
    Stops every process start-demo.ps1 started, including the ffmpeg feeders MediaMTX spawned
    and the child processes of "dotnet run".
#>
. "$PSScriptRoot\common.ps1"

$pidFile = Join-Path $DataRoot 'demo-processes.json'
if (-not (Test-Path $pidFile)) { Write-Ok 'Nothing to stop.'; return }

$running = Get-Content $pidFile -Raw | ConvertFrom-Json
foreach ($entry in $running.PSObject.Properties) {
    if (Get-Process -Id $entry.Value -ErrorAction SilentlyContinue) {
        # /T takes the whole tree: dotnet run -> the app, MediaMTX -> its ffmpeg feeders,
        # the node -> its Vision Service and ffmpeg recorders.
        & taskkill.exe /PID $entry.Value /T /F | Out-Null
        Write-Ok "Stopped $($entry.Name) (PID $($entry.Value))"
    }
}
Remove-Item $pidFile
