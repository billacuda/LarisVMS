# Shared helpers for the demo-site scripts. Dot-source it: . "$PSScriptRoot\common.ps1"

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DemoRoot   = $PSScriptRoot
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$DemoConfig = Get-Content (Join-Path $PSScriptRoot 'cameras.json') -Raw | ConvertFrom-Json
$DataRoot   = $DemoConfig.dataRoot
$BinDir     = Join-Path $PSScriptRoot 'bin'
$ClipsDir   = Join-Path $DataRoot 'clips'
$LogsDir    = Join-Path $DataRoot 'logs'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host $Message -ForegroundColor Green }

function Assert-Admin {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script needs an elevated (Run as administrator) PowerShell.'
    }
}

function Get-ToolPath([string]$Name) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "$Name was not found on PATH." }
    return $cmd.Source
}

# Runs T-SQL through ODBC rather than sqlcmd: the current sqlcmd (go-sqlcmd) can't use shared
# memory, and a fresh SQL Express install has named pipes and TCP turned off.
function Invoke-DemoSql([string]$Instance, [string]$Query) {
    $connection = [System.Data.Odbc.OdbcConnection]::new("Driver={ODBC Driver 18 for SQL Server};Server=$Instance;Trusted_Connection=Yes;TrustServerCertificate=Yes;")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = 120
        return $command.ExecuteScalar()
    } finally { $connection.Dispose() }
}

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}
