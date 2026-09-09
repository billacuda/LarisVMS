<#
.SYNOPSIS
    Install / upgrade the LarisVMS media proxy as a Windows Service on this machine.

.DESCRIPTION
    Failover plan phase 2. The media proxy is a standalone relay between browsers and recorder nodes
    for live view and playback — a dumb TLS-terminating pass-through. It holds no camera credentials
    and no per-node secret; only its own certificate and, at runtime, the list of node addresses it
    may forward to (fetched from the LarisVMS web server).

    On first run this registers with the server using the same one-time -RegistrationKey a recorder
    node uses; the assigned ProxyId/secret are persisted to %ProgramData%\LarisVMS\proxy.config
    (DPAPI, LocalMachine). Re-running the script to upgrade or change the port is safe — it does not
    re-register.

    LarisVMS.NodeUpdater.exe (bundled by build-proxy.ps1) is installed alongside LarisVMS.Proxy.exe so
    the proxy can auto-update itself: once an admin approves a newer proxy build on Admin -> Node
    Builds, the proxy downloads, verifies and applies it on its own next check-in, the same way a
    recorder node does. If it is missing the proxy just logs a warning and keeps running its current
    version until this script is re-run with a package that includes it.

    -ClientPort is required to bind the HTTPS listener. Supply -ClientPfxPath / -ClientPfxPassword
    for a real certificate, or -ClientAllowInsecure to have the proxy auto-generate a self-signed one
    (setup/testing only — viewers click through a browser warning, and the matching global toggle on
    Admin -> Settings -> Live View must also be on).

.EXAMPLE
    .\install-proxy.ps1 -ServerUrl https://vms.example.com -RegistrationKey abc123 -ClientPort 4443 -ClientPfxPath C:\certs\proxy.pfx -ClientPfxPassword hunter2

.EXAMPLE
    .\install-proxy.ps1 -ServerUrl https://vms.example.com -RegistrationKey abc123 -ClientPort 4443 -ClientAllowInsecure
#>

param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [Parameter(Mandatory)][string]$RegistrationKey,
    [string]$BinaryPath        = (Join-Path $PSScriptRoot 'LarisVMS.Proxy.exe'),
    # Detached binary-swap helper for auto-update — the same exe the recorder node uses, invoked with
    # --service LarisVMSProxy. ProxyUpdateService looks for it next to the running exe and just logs a
    # warning + skips the update if it's missing, so an older package without it still installs fine.
    [string]$UpdaterBinaryPath = (Join-Path $PSScriptRoot 'LarisVMS.NodeUpdater.exe'),
    [string]$InstallDir        = 'C:\Program Files\LarisVMS\Proxy',
    [string]$ServiceName       = 'LarisVMSProxy',
    [string]$ServiceDisplay    = 'LarisVMS Proxy',
    [switch]$InsecureTls,
    [pscredential]$ServiceCredential,

    [Parameter(Mandatory)][int]$ClientPort,
    [string]$ClientPfxPath      = '',
    [string]$ClientPfxPassword  = '',
    [switch]$ClientAllowInsecure,
    # FQDN browsers use to reach this proxy (must match the certificate). Defaults to the machine name.
    [string]$ClientEndpointHost = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }
function Format-ServiceArg([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}

if (-not (Test-Path $BinaryPath)) { throw "LarisVMS.Proxy.exe not found at $BinaryPath." }
if (-not (Test-Path $UpdaterBinaryPath)) {
    Write-Host "WARNING: Updater binary not found: $UpdaterBinaryPath - this proxy will not be able to auto-update until LarisVMS.NodeUpdater.exe is installed (re-run this script once it's available)." -ForegroundColor Yellow
}
if ($ClientPort -lt 1 -or $ClientPort -gt 65535) { throw "-ClientPort must be between 1 and 65535." }
if (-not $ClientPfxPath -and -not $ClientAllowInsecure) {
    Write-Host "NOTE: no -ClientPfxPath given and -ClientAllowInsecure not set. The proxy will only serve" -ForegroundColor Yellow
    Write-Host "      once a certificate path/password is set for it on Admin -> Media proxies." -ForegroundColor Yellow
}

# ── stop existing service ────────────────────────────────────────────────────
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
}

# ── install files ────────────────────────────────────────────────────────────
Write-Step "Installing proxy to $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$exePath = Join-Path $InstallDir 'LarisVMS.Proxy.exe'
for ($attempt = 1; $attempt -le 10; $attempt++) {
    try { Copy-Item $BinaryPath $exePath -Force; break }
    catch {
        if ($attempt -eq 10) { throw }
        Write-Host "    Copy attempt $attempt/10 failed (file in use) - retrying in 2s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}
if (Test-Path $UpdaterBinaryPath) {
    Copy-Item $UpdaterBinaryPath (Join-Path $InstallDir 'LarisVMS.NodeUpdater.exe') -Force
}
Write-Ok "Files installed"

# ── proxy-endpoint.json ──────────────────────────────────────────────────────
Write-Step "Writing proxy-endpoint.json (TCP $ClientPort)"
$cfgDir = Join-Path $env:ProgramData 'LarisVMS'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = [ordered]@{ port = $ClientPort; allowInsecure = [bool]$ClientAllowInsecure }
if ($ClientPfxPath)      { $cfg.pfxPath = $ClientPfxPath }
if ($ClientPfxPassword)  { $cfg.pfxPassword = $ClientPfxPassword }
if ($ClientEndpointHost) { $cfg.host = $ClientEndpointHost }
$cfg | ConvertTo-Json | Set-Content -Path (Join-Path $cfgDir 'proxy-endpoint.json') -Encoding UTF8
Write-Ok "Wrote $(Join-Path $cfgDir 'proxy-endpoint.json')"

# ── register / update service ────────────────────────────────────────────────
$argParts = @('--server-url', $ServerUrl, '--registration-key', $RegistrationKey)
if ($InsecureTls) { $argParts += '--insecure-tls' }
$binPath = (Format-ServiceArg $exePath) + ' ' + (($argParts | ForEach-Object { Format-ServiceArg $_ }) -join ' ')

if ($isUpgrade) {
    Write-Step "Updating existing service '$ServiceName'"
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    $changeArgs = @{ DisplayName = $ServiceDisplay; PathName = $binPath; StartMode = 'Automatic' }
    if ($ServiceCredential) {
        $changeArgs['StartName'] = $ServiceCredential.UserName
        $changeArgs['StartPassword'] = $ServiceCredential.GetNetworkCredential().Password
    }
    $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $changeArgs
    if ($result.ReturnValue -ne 0) { throw "Failed to update service '$ServiceName' (ReturnValue=$($result.ReturnValue))." }
    Write-Ok "Updated"
} else {
    Write-Step "Registering Windows Service '$ServiceName'"
    $serviceParams = @{
        Name           = $ServiceName
        DisplayName    = $ServiceDisplay
        Description    = 'LarisVMS media proxy - a TLS-terminating relay between browsers and recorder nodes.'
        BinaryPathName = $binPath
        StartupType    = 'Automatic'
    }
    if ($ServiceCredential) { $serviceParams['Credential'] = $ServiceCredential }
    New-Service @serviceParams | Out-Null
    Write-Ok "Registered"
}

Write-Step "Configuring service recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Write-Ok "Recovery configured"

# ── firewall ─────────────────────────────────────────────────────────────────
Write-Step "Configuring firewall (TCP $ClientPort)"
$ruleName = "LarisVMS Proxy Endpoint"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $ClientPort | Out-Null
Write-Ok "Allowed inbound TCP $ClientPort"

Write-Step "Starting service '$ServiceName'"
Start-Service -Name $ServiceName
Write-Ok "Started"

Write-Host "`nProxy $(if ($isUpgrade) { 'upgraded' } else { 'installed' }) successfully." -ForegroundColor Green
Write-Host "Install dir: $InstallDir"
Write-Host "Logs:        %ProgramData%\LarisVMS\logs\proxy-*.log"
Write-Host "Next: on Admin -> Media proxies, set this proxy's routable host and enable it, then assign nodes to it on Admin -> Nodes."
