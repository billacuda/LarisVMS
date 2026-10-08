<#
.SYNOPSIS
    Install or upgrade LarisVMS.Web as a self-hosted Kestrel Windows Service - no IIS required.

.DESCRIPTION
    Steps performed:
      1. Validates administrator privileges
      2. Guards against the recorder node / media proxy base <Version> drifting behind the release
         actually being deployed — see "Version sync guard" below. Skippable with
         -SkipVersionSyncCheck.
      3. Stops the existing 'LarisVMSWeb' service, if this is an upgrade (safe to re-run)
      4. Reads the connection string from the install directory's setup-generated.json, falling back
         to -LegacyIisConfigPath (an old deploy.ps1/IIS deployment's copy, for an in-place migration)
         if that's missing (or use -ConnectionString to override) — pre-setup, on a genuinely fresh
         install, there is none yet
      5. Guards against a misconfigured storage root — see "Storage root guard" below
      6. Builds and publishes the web project, self-contained win-x64 (skippable with -SkipBuild)
      7. Builds the recorder node package (build-node.ps1) and, optionally, the media proxy package
         (build-proxy.ps1) — same as deploy.ps1's own step, skippable with -SkipNodeBuild
      8. Bundles the node/proxy packages into the web publish (packages\) — the web app registers
         them as Pending on Admin -> Node Builds at startup
      9. Copies published files into $InstallDir via robocopy /MIR, explicitly preserving
         setup-generated.json, appsettings.Production.json, appsettings.Development.json,
         data-protection-keys\, and every recording/spool/export directory — never a blanket copy
     10. On a fresh install only (the file doesn't already exist), seeds appsettings.Production.json
         from the tracked appsettings.Production.json.example, so the service always has a starting
         config file to edit rather than none at all
     11. (Database migrations run in the web app itself on startup)
     12. Registers/updates the 'LarisVMSWeb' Windows Service (New-Service, or Win32_Service.Change on
         an upgrade — passing -ServiceCredential is optional on an upgrade; omitting it leaves the
         existing service logon account untouched)
     13. Sets ASPNETCORE_ENVIRONMENT=Production on the service's own registry Environment value
     14. Configures automatic restart on failure and an inbound firewall rule for -HttpsPort
     15. Starts the service and probes /health

    Domain/service account vs SQL Authentication: pass -ServiceCredential for a domain or local
    service account (needed for SQL Server Integrated Security against a domain instance); omit it to
    run as LocalSystem, which works fine with SQL Authentication, or with Integrated Security once
    this machine's own computer account is granted a SQL login.

    HTTPS certificate: comes from Kestrel:Certificates:Default:Path/:Password in
    appsettings.Production.json (seeded from the .example on a fresh install — edit it with your real
    certificate before going to production). Until a real certificate is configured, the service still
    comes up on a self-signed one so the setup wizard is always reachable.

    Must be run as Administrator (required for service/firewall management).

.EXAMPLE
    .\install-web.ps1
    .\install-web.ps1 -ServiceCredential (Get-Credential)
    .\install-web.ps1 -HttpsPort 8444 -BuildProxy
#>

param(
    [string]$WebProject            = (Join-Path $PSScriptRoot 'src\LarisVMS.Web\LarisVMS.Web.csproj'),
    [string]$PublishDir            = (Join-Path $PSScriptRoot 'publish\LarisVMS.Web'),
    [string]$Configuration         = 'Release',
    [string]$InstallDir            = 'C:\Program Files\LarisVMS\Web',
    [string]$ServiceName           = 'LarisVMSWeb',
    [string]$ServiceDisplay        = 'LarisVMS Web',
    # Non-standard default per design: this is a pre-release, single-operator deployment on a machine
    # that may already run other sites on 80/443.
    [int]$HttpsPort                = 8444,
    [pscredential]$ServiceCredential,
    [string]$ConnectionString      = '',
    [string]$ExtraNodePublishPath  = '',
    [string]$NodeCsprojPath        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj'),
    [string]$ProxyCsprojPath       = (Join-Path $PSScriptRoot 'src\LarisVMS.Proxy\LarisVMS.Proxy.csproj'),
    [string]$NodeUpdaterCsprojPath = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj'),
    [string]$ChangelogPath         = (Join-Path $PSScriptRoot 'CHANGELOG.md'),
    [string]$MigrationsPath        = (Join-Path $PSScriptRoot 'src\LarisVMS.Infrastructure\Migrations'),
    [switch]$SkipNodeVision,
    [switch]$SkipBuild,
    [switch]$SkipNodeBuild,
    [switch]$SkipHealthCheck,
    [switch]$SkipVersionSyncCheck,
    [switch]$BuildProxy,
    # Falls back here when $InstallDir has no setup-generated.json of its own yet — the exact gap hit
    # when migrating an existing IIS deployment to this script: deploy.ps1's own default
    # -DestinationPath, where an in-place upgrade's setup-generated.json still lives untouched. Set to
    # '' to disable the fallback entirely.
    [string]$LegacyIisConfigPath   = 'E:\Sites\LarisVMS\setup-generated.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region helpers

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host $Message -ForegroundColor Green
}

function Invoke-Cmd([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-SqlClientConnectionString([string]$ConnectionString) {
    # Same reasoning as deploy.ps1's own copy of this helper: System.Data.SqlClient (used here so this
    # script has no extra assembly to load) rejects keywords Microsoft.Data.SqlClient writes into
    # setup-generated.json — comparing the whitespace-stripped key is what actually catches
    # "Trust Server Certificate" with the spaces SqlConnectionStringBuilder itself emits.
    $forbiddenKeys = @('encrypt', 'trustservercertificate', 'multipleactiveresultsets', 'applicationintent')
    return ($ConnectionString -split ';' | Where-Object {
        $key = ($_ -split '=', 2)[0] -replace '\s', ''
        $forbiddenKeys -notcontains $key.ToLowerInvariant()
    }) -join ';'
}

#endregion

# ── admin check ───────────────────────────────────────────────────────────────

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator (required for service/firewall management)."
}

# ── version sync guard ────────────────────────────────────────────────────────
# See tools\VersionGuard.ps1 — shared with build-installers.ps1.
. (Join-Path $PSScriptRoot 'tools\VersionGuard.ps1')
if (-not $SkipVersionSyncCheck) {
    Write-Step "Checking web/node/proxy version is in sync with this release"
    if (-not (Test-Path $ChangelogPath)) {
        Write-Host "CHANGELOG.md not found at '$ChangelogPath' - skipping version sync guard."
    } else {
        $releaseVersion = Assert-ReleaseVersionSync -ChangelogPath $ChangelogPath -MigrationsPath $MigrationsPath -Projects @{
            'LarisVMS.Web' = $WebProject; 'LarisVMS.Node' = $NodeCsprojPath
            'LarisVMS.Proxy' = $ProxyCsprojPath; 'LarisVMS.NodeUpdater' = $NodeUpdaterCsprojPath
        }
        Write-Ok "Web/Node/Proxy/NodeUpdater version and the AppVersions migration match this release ($releaseVersion)."
    }
} else {
    Write-Host "Skipping version sync guard (-SkipVersionSyncCheck)."
}

# ── stop existing service (upgrade detection) ────────────────────────────────
# Must happen before anything below touches $InstallDir — a running service's own exe/dll can be
# locked while it's up, so overwriting them mid-run can fail. Deliberately stop-only, not
# delete+recreate, mirroring install-node.ps1's own reasoning: an upgrade updates the same
# ServiceName in place further down instead of tearing it down and rebuilding it. Unlike
# install-node.ps1, there's no orphaned-child-process concern here — LarisVMS.Web spawns nothing.

$isUpgrade = $false
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    $isUpgrade = $true
    Write-Step "Stopping existing service '$ServiceName'"
    if ($existingSvc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existingSvc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Write-Ok "Stopped"
    } else {
        Write-Host "    Already stopped."
    }
}

# ── resolve connection string ─────────────────────────────────────────────────
# Needed by the storage-root guard and the media-port firewall rule below.

$installedConfigFile = Join-Path $InstallDir 'setup-generated.json'

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Step "Reading connection string from installed config"
    $configFileRead = $installedConfigFile
    if (-not (Test-Path $installedConfigFile) -and -not [string]::IsNullOrWhiteSpace($LegacyIisConfigPath) -and (Test-Path $LegacyIisConfigPath)) {
        # An in-place IIS -> Kestrel migration: the old deploy.ps1 site never wrote its
        # setup-generated.json into this script's own -InstallDir, and the service needs it there to
        # find its database.
        Write-Host "    Not found at '$installedConfigFile' - falling back to legacy IIS path '$LegacyIisConfigPath'."
        $configFileRead = $LegacyIisConfigPath
    }
    if (Test-Path $configFileRead) {
        try {
            $ConnectionString = (Get-Content $configFileRead -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
        } catch {
            Write-Host "Could not parse '$configFileRead': $_"
        }
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        Write-Warning "No connection string found at '$installedConfigFile' or '$LegacyIisConfigPath'."
        Write-Warning "The storage-root guard is skipped this run. Fine on a fresh install (the Setup wizard creates the database); otherwise pass -ConnectionString or copy setup-generated.json from the old deployment into '$InstallDir'."
    } else {
        Write-Ok "Connection string loaded from '$configFileRead'."
    }
}

# ── storage root guard ────────────────────────────────────────────────────────
# Identical reasoning to deploy.ps1's own guard: robocopy /MIR below mirrors the publish output onto
# $InstallDir — anything present at the destination that isn't in the publish output is deleted. If
# any node's storage (or archive) root were configured to a path under the install directory, this
# would silently delete every recording under it on the next upgrade.
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    try {
        Add-Type -AssemblyName System.Data
        $sqlCs = Get-SqlClientConnectionString $ConnectionString

        $conn = New-Object System.Data.SqlClient.SqlConnection($sqlCs)
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT StorageRootPath, ArchiveRootPath FROM Nodes"
        $reader = $cmd.ExecuteReader()
        $nodePaths = @()
        while ($reader.Read()) {
            if (-not $reader.IsDBNull(0)) { $nodePaths += $reader.GetString(0) }
            if (-not $reader.IsDBNull(1)) { $nodePaths += $reader.GetString(1) }
        }
        $reader.Close()
        $conn.Close()

        $normalizedDest = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
        foreach ($p in $nodePaths) {
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            try { $normalizedStorage = [System.IO.Path]::GetFullPath($p).TrimEnd('\') + '\' }
            catch { continue }
            if ($normalizedStorage.StartsWith($normalizedDest, [StringComparison]::OrdinalIgnoreCase)) {
                throw "A recorder node's storage/archive path '$p' resolves under the install directory " +
                      "'$InstallDir'. A robocopy /MIR upgrade would delete every recording under it. " +
                      "Move that node's path (Admin -> Nodes) outside the install directory before upgrading."
            }
        }
    } catch [System.Management.Automation.RuntimeException] {
        throw
    } catch {
        Write-Host "Could not read node storage paths for the storage-root guard: $_"
    }
} else {
    Write-Host "No connection string available - skipping storage-root guard."
}

# ── build and publish ─────────────────────────────────────────────────────────

if (-not $SkipBuild) {
    Write-Step "Building and publishing ($Configuration)"

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    # Self-contained, same as the Web MSI: no ASP.NET Core runtime needed on the server.
    Invoke-Cmd 'dotnet' @('publish', $WebProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', '-o', $PublishDir)
    Write-Ok "Published to: $PublishDir"
} else {
    Write-Host "Build/publish skipped (-SkipBuild) - installing whatever is already in $PublishDir."
}

# ── build recorder node package ───────────────────────────────────────────────
# Same as deploy.ps1's own step — keeps publish\LarisVMS.Node\win current with every web install.

if (-not $SkipNodeBuild) {
    Write-Step "Building recorder node package"
    $buildNodeScript = Join-Path $PSScriptRoot 'build-node.ps1'
    $buildNodeArgs = @{ Configuration = $Configuration }
    if (-not [string]::IsNullOrWhiteSpace($ExtraNodePublishPath)) {
        $buildNodeArgs['ExtraPublishPath'] = $ExtraNodePublishPath
    }
    if ($SkipNodeVision) {
        $buildNodeArgs['SkipVision'] = $true
    }
    & $buildNodeScript @buildNodeArgs
    Write-Ok "Node package built."
} else {
    Write-Host "Node package build skipped (-SkipNodeBuild)."
}

if ($BuildProxy) {
    Write-Step "Building media proxy package"
    $buildProxyScript = Join-Path $PSScriptRoot 'build-proxy.ps1'
    $buildProxyArgs = @{ Configuration = $Configuration }
    if (-not [string]::IsNullOrWhiteSpace($ExtraNodePublishPath)) {
        $buildProxyArgs['ExtraPublishPath'] = ($ExtraNodePublishPath.TrimEnd('\') + '-proxy')
    }
    & $buildProxyScript @buildProxyArgs
    Write-Ok "Media proxy package built (publish\LarisVMS.Proxy\win)."
}

# ── bundle node/proxy packages into the web publish ──────────────────────────
# The web app registers these as Pending on Admin -> Node Builds at startup (IBundledBuildRegistrar).
$packagesDir = Join-Path $PublishDir 'packages'
$bundles = @(
    @{ Src = 'publish\LarisVMS.Node\win\LarisVMS.Node.exe';             Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Node\win\LarisVMS.Vision.Service.exe';   Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Node\node-build-version.txt';            Dest = 'node' },
    @{ Src = 'publish\LarisVMS.Proxy\win\LarisVMS.Proxy.exe';           Dest = 'proxy' },
    @{ Src = 'publish\LarisVMS.Proxy\proxy-build-version.txt';          Dest = 'proxy' },
    @{ Src = 'publish\LarisVMS.Node\cuda-provider\onnxruntime_providers_cuda.dll'; Dest = 'cuda-provider' }
)
foreach ($b in $bundles) {
    $src = Join-Path $PSScriptRoot $b.Src
    if (Test-Path $src) {
        $dest = Join-Path $packagesDir $b.Dest
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Copy-Item $src $dest -Force
    }
}

# ── copy files, without clobbering machine-specific config ───────────────────
# /MIR mirrors: anything at the destination not in the publish output is deleted.
#   - setup-generated.json / appsettings.Production.json / appsettings.Development.json: generated by
#     the wizard or hand-edited on this machine, hold the connection string and cert password in the
#     clear, never published.
#   - data-protection-keys / recordings / spool / exports / logs: same reasoning as deploy.ps1's own
#     exclusion list.

Write-Step "Copying files to $InstallDir"

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$rcArgs = @(
    $PublishDir, $InstallDir,
    '/MIR',
    '/XF', 'setup-generated.json', 'appsettings.Production.json', 'appsettings.Development.json',
    '/XD', 'data-protection-keys', 'recordings', 'spool', 'exports', 'logs',
    '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS'
)
robocopy @rcArgs
if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE." }

Write-Ok "Files installed to $InstallDir"

# ── seed appsettings.Production.json from the example, fresh install only ────
# Mirrors SideGlance\install.ps1's own "seed from bundled example if missing" pattern. A genuinely
# fresh install has nothing at $realPath yet (the /XF exclusion above only preserves a file that
# already existed) — an upgrade's real file was already preserved and is left untouched.
$realProdConfig = Join-Path $InstallDir 'appsettings.Production.json'
$exampleProdConfig = Join-Path $InstallDir 'appsettings.Production.json.example'
if (-not (Test-Path $realProdConfig)) {
    if (Test-Path $exampleProdConfig) {
        Copy-Item $exampleProdConfig $realProdConfig
        Write-Host "Wrote a starter appsettings.Production.json to $realProdConfig — edit the Kestrel certificate Path/Password before going to production (the service still starts on a self-signed certificate meanwhile)." -ForegroundColor Yellow
    } else {
        Write-Host "WARNING: appsettings.Production.json.example not found in the publish output — no appsettings.Production.json was seeded. The service will run without a configured HTTPS certificate (self-signed) until you create one." -ForegroundColor Yellow
    }
}

# Database migrations, node/proxy build registration and CUDA provider seeding happen in the web
# app itself at startup (Program.cs: MigrateAsync + IBundledBuildRegistrar), using the
# packages\ folder bundled into the publish output above.

# ── register / update Windows Service ─────────────────────────────────────────
# All config (connection string, cert path, ports) comes from setup-generated.json /
# appsettings.Production.json — unlike install-node.ps1, no registration arguments need baking into
# the service command line.

$exePath = Join-Path $InstallDir 'LarisVMS.Web.exe'
$binPath = if ($exePath -match '[\s"]') { '"' + ($exePath -replace '"', '\"') + '"' } else { $exePath }

# Report the account the service will actually run as. Without -ServiceCredential an upgrade keeps
# the existing service's account (Win32_Service.Change below leaves StartName alone), so read it
# rather than assuming LocalSystem.
$runAs = if ($ServiceCredential) { $ServiceCredential.UserName }
         elseif ($isUpgrade) { (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'").StartName }
         else { 'LocalSystem' }
$runAsSource = if ($ServiceCredential) { '' } elseif ($isUpgrade) { ' (kept from the existing service)' } else { '' }
if ($runAs -and $runAs -notin 'LocalSystem', '.\LocalSystem', 'NT AUTHORITY\SYSTEM') {
    Write-Host "    Running as $runAs$runAsSource — with Integrated Security this account needs a SQL Server login with db_owner on the LarisVMS database."
} else {
    $computerAccount = if ((Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain) { "$env:USERDOMAIN\$env:COMPUTERNAME`$" } else { "$env:COMPUTERNAME`$" }
    Write-Host "    Running as LocalSystem$runAsSource." -ForegroundColor Yellow
    Write-Host "    If using SQL Server Integrated Security, LocalSystem authenticates to SQL as this" -ForegroundColor Yellow
    Write-Host "    machine's own computer account ($computerAccount) - grant that account a" -ForegroundColor Yellow
    Write-Host "    SQL login, or use SQL Authentication instead during Setup, or re-run with" -ForegroundColor Yellow
    Write-Host "    -ServiceCredential (Get-Credential) for a domain/service account." -ForegroundColor Yellow
}

if ($isUpgrade) {
    # Win32_Service.Change, not sc.exe config — same reasoning as install-node.ps1's own comment:
    # Change() takes PathName as a normal .NET string, sidestepping sc.exe's quoting minefield.
    # Any Change() parameter left out of $changeArgs (here: StartName/StartPassword when
    # -ServiceCredential isn't passed) is left as-is by WMI, not reset — this is how "credential
    # optional on upgrade, existing account preserved" is achieved with no special-case code.
    Write-Step "Updating existing service '$ServiceName'"
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    $changeArgs = @{
        DisplayName = $ServiceDisplay
        PathName    = $binPath
        StartMode   = 'Automatic'
    }
    if ($ServiceCredential) {
        $changeArgs['StartName'] = $ServiceCredential.UserName
        $changeArgs['StartPassword'] = $ServiceCredential.GetNetworkCredential().Password
    }
    $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $changeArgs
    if ($result.ReturnValue -ne 0) {
        throw "Failed to update service '$ServiceName' (Win32_Service.Change ReturnValue=$($result.ReturnValue))."
    }
    Write-Ok "Updated"
} else {
    Write-Step "Registering Windows Service '$ServiceName'"
    $serviceParams = @{
        Name           = $ServiceName
        DisplayName    = $ServiceDisplay
        Description    = 'LarisVMS web tier - Razor Pages admin UI + node control plane API, self-hosted Kestrel.'
        BinaryPathName = $binPath
        StartupType    = 'Automatic'
    }
    if ($ServiceCredential) { $serviceParams['Credential'] = $ServiceCredential }
    New-Service @serviceParams | Out-Null
    Write-Ok "Registered"
}

# ── ASPNETCORE_ENVIRONMENT ────────────────────────────────────────────────────
# Replaces web.config's old hardcoded-to-Development-with-a-warning value with something that's
# always correct. Set unconditionally on both fresh install and upgrade — there's no parameter to
# override it, on purpose (Program.cs also carries a belt-and-suspenders fallback for the case this
# registry value is ever missing on an installed service).
Write-Step "Setting ASPNETCORE_ENVIRONMENT=Production"
$svcRegKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
# RegistryKey.SetValue only accepts a real string[] for REG_MULTI_SZ, not PowerShell's untyped
# Object[] from a bare @(...) literal — an explicit [string[]] cast is required or Set-ItemProperty
# throws "does not support arrays of type 'Object[]'".
Set-ItemProperty -Path $svcRegKey -Name Environment -Value ([string[]]@('ASPNETCORE_ENVIRONMENT=Production'))
Write-Ok "Set"

# ── configure service recovery ────────────────────────────────────────────────

Write-Step "Configuring service recovery"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Write-Ok "Recovery configured (restart on 1st/2nd/3rd failure; reset after 1 day)"

# ── firewall ──────────────────────────────────────────────────────────────────

Write-Step "Configuring firewall for HTTPS (TCP $HttpsPort)"
$firewallRuleName = "LarisVMS Web"
Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort | Out-Null
Write-Ok "Allowed inbound TCP $HttpsPort"

# Best-effort: if LiveView.CustomPort is already configured in an existing database (an upgrade),
# also allow that port. A truly fresh box has no Settings table yet to read this from — the admin can
# always add the rule by hand once they configure the setting (Admin -> Settings -> Live View).
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    try {
        Add-Type -AssemblyName System.Data
        $sqlCs = Get-SqlClientConnectionString $ConnectionString
        $conn = New-Object System.Data.SqlClient.SqlConnection($sqlCs)
        $conn.Open()
        try {
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = "SELECT Value FROM Settings WHERE [Key] = 'LiveView.CustomPort'"
            $rawMediaPort = $cmd.ExecuteScalar()
            if ($rawMediaPort -and [int]::TryParse($rawMediaPort, [ref]$null)) {
                $mediaPort = [int]$rawMediaPort
                if ($mediaPort -gt 0) {
                    $mediaFirewallRuleName = "LarisVMS Web Media Port"
                    Get-NetFirewallRule -DisplayName $mediaFirewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                    New-NetFirewallRule -DisplayName $mediaFirewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $mediaPort | Out-Null
                    Write-Ok "Allowed inbound TCP $mediaPort (LiveView.CustomPort)"
                }
            }
        } finally {
            $conn.Close()
        }
    } catch {
        Write-Host "Could not check LiveView.CustomPort for a firewall rule: $_" -ForegroundColor Yellow
    }
}

# ── start ─────────────────────────────────────────────────────────────────────

Write-Step "Starting service '$ServiceName'"
Start-Service -Name $ServiceName
Write-Ok "Started"

# ── post-install health probe ─────────────────────────────────────────────────

$healthUrl = "https://localhost:$HttpsPort/health"
if (-not $SkipHealthCheck) {
    Write-Step "Probing /health"
    $healthy = $false
    for ($i = 0; $i -lt 10; $i++) {
        try {
            # -SkipCertificateCheck: a fresh install may still be on the auto-generated self-signed
            # certificate at this point (see Program.cs's CertHolder AllowInsecure:true fallback) —
            # this probe only cares whether something is listening and answering, not certificate trust.
            $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5 -SkipCertificateCheck
            if ($resp.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if ($healthy) {
        Write-Ok "Health check passed ($healthUrl)."
    } else {
        Write-Host "Health check did not return 200 within 20 s ($healthUrl). The app may still be starting." -ForegroundColor Yellow
    }
}

Write-Host "`nLarisVMS Web $(if ($isUpgrade) { 'upgraded' } else { 'installed' }) successfully." -ForegroundColor Green
Write-Host "Browse to https://<host>:$HttpsPort/ to continue setup."
if (-not (Test-Path (Join-Path $InstallDir 'appsettings.Production.json')) -or $ConnectionString -eq '') {
    Write-Host "Reminder: edit appsettings.Production.json's certificate Path/Password before production use." -ForegroundColor Yellow
}
