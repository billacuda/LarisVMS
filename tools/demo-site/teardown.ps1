<#
.SYNOPSIS
    Removes everything the demo site created on this machine.

.DESCRIPTION
    Stops the demo, drops the LarisVMS_Demo database, deletes <dataRoot> (clips, recordings, logs)
    and the node/web state under %ProgramData%\LarisVMS, removes the Web's setup-generated.json,
    and removes the demo network. SQL Server Express itself stays installed.

    Only run this on the demo machine: %ProgramData%\LarisVMS is where a real node or web server
    keeps its registration and encryption keys.

    Needs an elevated PowerShell (for the network and %ProgramData%).
#>
param(
    [string]$SqlInstance = '.\SQLEXPRESS',
    [string]$Database = 'LarisVMS_Demo',
    [switch]$KeepClips
)

. "$PSScriptRoot\common.ps1"
Assert-Admin

& "$PSScriptRoot\stop-demo.ps1"

Write-Step "Dropping database $Database"
try {
    Invoke-DemoSql $SqlInstance "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END" | Out-Null
    Write-Ok 'Dropped.'
} catch { Write-Warning "Could not drop $Database ($($_.Exception.Message)); drop it yourself." }

Write-Step 'Deleting demo data'
foreach ($dir in 'recordings', 'logs', 'exports') {
    Remove-Item (Join-Path $DataRoot $dir) -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $DataRoot 'mediamtx.yml') -ErrorAction SilentlyContinue
if (-not $KeepClips) { Remove-Item $DataRoot -Recurse -Force -ErrorAction SilentlyContinue }

$programData = Join-Path $env:ProgramData 'LarisVMS'
Remove-Item $programData -Recurse -Force -ErrorAction SilentlyContinue
# "dotnet run" uses the project folder as the content root, so the wizard writes it there (gitignored).
Remove-Item (Join-Path $RepoRoot 'src\LarisVMS.Web\setup-generated.json') -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $RepoRoot 'src\LarisVMS.Web\bin') -Recurse -Filter 'setup-generated.json' -ErrorAction SilentlyContinue |
    Remove-Item -Force

& "$PSScriptRoot\setup-network.ps1" -Remove
Write-Ok "`nDemo removed."
