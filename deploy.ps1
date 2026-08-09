<#
.SYNOPSIS
    Deploy NidusVMS's web tier to IIS - applies EF migrations, publishes the web app.

.DESCRIPTION
    Steps performed:
      1. Validates administrator privileges
      2. Resolves IIS destination path and app pool from the provided parameters
      3. Restores dotnet local tools (dotnet-ef)
      4. Reads the connection string from setup-generated.json at the destination
         (or use -ConnectionString to override)
      5. Guards against a misconfigured storage root — see "Storage root guard" below
      6. Builds and publishes the web project
      7. Stops the IIS app pool
      8. Applies any pending EF Core migrations
      9. Copies published files to the IIS site, preserving setup-generated.json,
         appsettings.Production.json, data-protection-keys\, and every recording/spool/export
         directory
     10. Starts the IIS app pool (always, even on failure)
     11. Probes /health once the pool is back up

    This script only deploys NidusVMS.Web. Recorder nodes are separate Windows Services deployed with
    build-node.ps1 / install-node.ps1 (milestone M3) — they are not part of the IIS site and must
    never be inside $DestinationPath.

    Must be run as Administrator (required for IIS management).

.EXAMPLE
    .\deploy.ps1 -IISSiteName "NidusVMS"
    .\deploy.ps1 -IISSiteName "NidusVMS" -SkipMigrations
    .\deploy.ps1 -IISSiteUrl "https://nidusvms.example.com"
#>

param(
    [string]$WebProject        = (Join-Path $PSScriptRoot 'src\NidusVMS.Web\NidusVMS.Web.csproj'),
    [string]$MigrationsProject = (Join-Path $PSScriptRoot 'src\NidusVMS.Infrastructure\NidusVMS.Infrastructure.csproj'),
    [string]$PublishDir        = (Join-Path $PSScriptRoot 'publish\NidusVMS.Web'),
    [string]$Configuration     = 'Release',
    [string]$DestinationPath   = 'E:\Sites\NidusVMS', # will be overridden if IIS site or URL is specified
    [string]$IISAppPoolName    = 'NidusVMS',
    [string]$IISSiteName       = 'NidusVMS',
    [string]$IISSiteUrl        = '',
    [string]$ConnectionString  = '',
    [switch]$SkipMigrations,
    [switch]$SkipHealthCheck
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

function Get-IISSiteByUrl([string]$SiteUrl) {
    try { $uri = [uri]$SiteUrl } catch { throw "Invalid IIS site URL: $SiteUrl" }
    foreach ($s in Get-Website) {
        foreach ($b in $s.Bindings.Collection) {
            $parts = $b.bindingInformation -split ':'
            if ($parts.Length -lt 3) { continue }
            if ([int]$parts[1] -eq $uri.Port -and $b.protocol -eq $uri.Scheme) {
                if ($parts[2] -eq $uri.Host -or $parts[2] -eq '*' -or [string]::IsNullOrWhiteSpace($parts[2])) {
                    return $s
                }
            }
        }
    }
    return $null
}

#endregion

# ── admin check ───────────────────────────────────────────────────────────────

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run as Administrator (required for IIS management)."
}

# ── resolve IIS target ────────────────────────────────────────────────────────

Write-Step "Resolving IIS deployment target"
Import-Module WebAdministration -ErrorAction Stop

if (-not [string]::IsNullOrWhiteSpace($IISSiteUrl)) {
    $site = Get-IISSiteByUrl -SiteUrl $IISSiteUrl
    if (-not $site) { throw "No IIS site matches URL '$IISSiteUrl'." }
    $IISSiteName = $site.Name

    $urlPath = ([uri]$IISSiteUrl).AbsolutePath.TrimEnd('/')
    if (-not [string]::IsNullOrWhiteSpace($urlPath) -and $urlPath -ne '/') {
        $vApp = Get-WebApplication -Site $site.Name | Where-Object { $_.Path.TrimEnd('/') -eq $urlPath }
        if ($vApp) {
            $DestinationPath = [Environment]::ExpandEnvironmentVariables($vApp.PhysicalPath)
            Write-Host "Resolved virtual app '$urlPath' -> '$DestinationPath'."
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($IISSiteName)) {
    $site = Get-Website -Name $IISSiteName -ErrorAction Stop
    if (-not $site) { throw "IIS site '$IISSiteName' not found." }
    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        $DestinationPath = [Environment]::ExpandEnvironmentVariables($site.PhysicalPath)
    }
    if ([string]::IsNullOrWhiteSpace($IISAppPoolName)) {
        $IISAppPoolName = $site.ApplicationPool
    }
}

if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    throw "DestinationPath could not be determined. Provide -DestinationPath, -IISSiteName, or -IISSiteUrl."
}

$poolLabel = if ([string]::IsNullOrWhiteSpace($IISAppPoolName)) { '(not specified)' } else { $IISAppPoolName }
Write-Ok "Destination : $DestinationPath"
Write-Ok "App pool    : $poolLabel"

# ── restore dotnet tools ──────────────────────────────────────────────────────

Write-Step "Restoring dotnet local tools"
Invoke-Cmd 'dotnet' @('tool', 'restore')
Write-Ok "Tools ready."

# ── resolve connection string ─────────────────────────────────────────────────

$runMigrations = -not $SkipMigrations.IsPresent
$deployedConfigFile = Join-Path $DestinationPath 'setup-generated.json'

if ($runMigrations -and [string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Step "Reading connection string from deployed config"
    if (Test-Path $deployedConfigFile) {
        try {
            $ConnectionString = (Get-Content $deployedConfigFile -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
        } catch {
            Write-Host "Could not parse '$deployedConfigFile': $_"
        }
    }

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        Write-Host "No connection string found at '$deployedConfigFile'."
        Write-Host "Migrations will be skipped. Run the Setup wizard first, or pass -ConnectionString explicitly."
        $runMigrations = $false
    } else {
        Write-Ok "Connection string loaded from setup-generated.json."
    }
}

# ── storage root guard ────────────────────────────────────────────────────────
# robocopy /MIR below mirrors the publish output onto $DestinationPath — anything present at the
# destination that isn't in the publish output is deleted. If a camera's storage root were ever
# configured to a path under the IIS site directory, this would silently delete every recording on
# the next deploy. Storage.RootPath is a Settings-table row (SetupService.SaveStorageRootAsync),
# not part of setup-generated.json, so this queries it straight from the database using the same
# connection string already resolved above for migrations. Best-effort only — if the query fails
# for any reason (pre-setup, migrations skipped, no -ConnectionString available) the guard is
# skipped with a warning rather than blocking the deploy, and is not a substitute for keeping
# recordings outside the site directory entirely.
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    try {
        Add-Type -AssemblyName System.Data
        # System.Data.SqlClient (the legacy provider, used here rather than Microsoft.Data.SqlClient
        # so this script has no extra assembly to load) rejects keywords Microsoft.Data.SqlClient
        # writes into setup-generated.json — including "Trust Server Certificate" *with the spaces
        # SqlConnectionStringBuilder itself emits*, which a whitespace-sensitive regex match misses.
        # Comparing the whitespace-stripped key is what actually catches it.
        $forbiddenKeys = @('encrypt', 'trustservercertificate', 'multipleactiveresultsets', 'applicationintent')
        $sqlCs = ($ConnectionString -split ';' | Where-Object {
            $key = ($_ -split '=', 2)[0] -replace '\s', ''
            $forbiddenKeys -notcontains $key.ToLowerInvariant()
        }) -join ';'

        $conn = New-Object System.Data.SqlClient.SqlConnection($sqlCs)
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT Value FROM Settings WHERE [Key] = 'Storage.RootPath'"
        $storageRoot = $cmd.ExecuteScalar()
        $conn.Close()

        if (-not [string]::IsNullOrWhiteSpace($storageRoot)) {
            $normalizedDest = [System.IO.Path]::GetFullPath($DestinationPath).TrimEnd('\') + '\'
            $normalizedStorage = [System.IO.Path]::GetFullPath($storageRoot).TrimEnd('\') + '\'
            if ($normalizedStorage.StartsWith($normalizedDest, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Storage root '$storageRoot' resolves under the IIS site directory '$DestinationPath'. " +
                      "A robocopy /MIR deploy would delete every recording under it. Move the storage root " +
                      "outside the site directory (Admin -> Storage) before deploying."
            }
        }
    } catch [System.Management.Automation.RuntimeException] {
        throw
    } catch {
        Write-Host "Could not read Storage.RootPath for the storage-root guard: $_"
    }
} else {
    Write-Host "No connection string available - skipping storage-root guard."
}

# ── build and publish ─────────────────────────────────────────────────────────

Write-Step "Building and publishing ($Configuration)"

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Invoke-Cmd 'dotnet' @('publish', $WebProject, '-c', $Configuration, '-o', $PublishDir)
Write-Ok "Published to: $PublishDir"

# ── stop app pool ─────────────────────────────────────────────────────────────

$hasPool = -not [string]::IsNullOrWhiteSpace($IISAppPoolName)

if ($hasPool) {
    Write-Step "Stopping app pool '$IISAppPoolName'"
    $state = (Get-WebAppPoolState -Name $IISAppPoolName -ErrorAction Stop).Value
    if ($state -ne 'Stopped') {
        Stop-WebAppPool -Name $IISAppPoolName
        $elapsed = 0
        while ((Get-WebAppPoolState -Name $IISAppPoolName).Value -ne 'Stopped' -and $elapsed -lt 30) {
            Start-Sleep -Seconds 1
            $elapsed++
        }
        if ((Get-WebAppPoolState -Name $IISAppPoolName).Value -ne 'Stopped') {
            Write-Host "App pool did not stop within 30 s - continuing anyway."
        } else {
            Write-Ok "App pool stopped."
        }
    } else {
        Write-Host "App pool was already stopped."
    }
} else {
    Write-Host "No app pool specified - IIS pool will not be managed."
}

# ── migrate + copy (pool always restarted in finally) ────────────────────────

try {
    if ($runMigrations) {
        Write-Step "Applying EF Core migrations"
        Invoke-Cmd 'dotnet' @(
            'ef', 'database', 'update',
            '--project',         $MigrationsProject,
            '--startup-project', $WebProject,
            '--configuration',   $Configuration,
            '--no-build',
            '--connection',      $ConnectionString
        )
        Write-Ok "Migrations applied."
    } else {
        Write-Host "`nMigrations skipped."
    }

    Write-Step "Copying files to IIS site"

    if (-not (Test-Path $DestinationPath)) {
        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    }

    # /MIR mirrors: anything at the destination not in the publish output is deleted.
    #   - setup-generated.json / appsettings.Production.json: generated by the wizard, hold the
    #     connection string in the clear, never published.
    #   - data-protection-keys: the key ring — wiping it signs everyone out and, once camera/SMB
    #     credentials are stored under it, would destroy them outright.
    #   - recordings / spool / exports: the storage-root guard above only catches the configured
    #     default root; these names are excluded unconditionally as a second line of defense for
    #     anyone who still points a storage target inside the site directory.
    $rcArgs = @(
        $PublishDir, $DestinationPath,
        '/MIR',
        '/XF', 'setup-generated.json', 'appsettings.Production.json',
        '/XD', 'data-protection-keys', 'recordings', 'spool', 'exports',
        '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS'
    )
    robocopy @rcArgs
    if ($LASTEXITCODE -ge 8) { throw "Robocopy failed with exit code $LASTEXITCODE." }

    Write-Ok "Files deployed to $DestinationPath"
} finally {
    if ($hasPool) {
        Write-Step "Starting app pool '$IISAppPoolName'"
        Start-WebAppPool -Name $IISAppPoolName
        Write-Ok "App pool started."
    }
}

# ── post-deploy health probe ──────────────────────────────────────────────────

if (-not $SkipHealthCheck -and -not [string]::IsNullOrWhiteSpace($IISSiteUrl)) {
    Write-Step "Probing /health"
    $healthUrl = ([uri]$IISSiteUrl).GetLeftPart([UriPartial]::Authority) + '/health'
    $healthy = $false
    for ($i = 0; $i -lt 10; $i++) {
        try {
            $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5
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

Write-Host "`nDeployment complete!" -ForegroundColor Green
