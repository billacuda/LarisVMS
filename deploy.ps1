<#
.SYNOPSIS
    Deploy LarisVMS's web tier to IIS - applies EF migrations, publishes the web app.

.DESCRIPTION
    Steps performed:
      1. Validates administrator privileges
      2. Resolves IIS destination path and app pool from the provided parameters
      3. Restores dotnet local tools (dotnet-ef)
      4. Reads the connection string from setup-generated.json at the destination
         (or use -ConnectionString to override)
      5. Guards against a misconfigured storage root — see "Storage root guard" below
      6. Builds and publishes the web project
      7. Builds the recorder node package (build-node.ps1) so publish\LarisVMS.Node\win — and,
         if -ExtraNodePublishPath is given, that second location too — stays current with every
         deploy instead of only when someone remembers to run build-node.ps1 by hand. Skippable
         with -SkipNodeBuild.
      8. Registers that build with LarisVMS.Web's node-build-approval queue (Admin -> Node Builds)
         as Pending, writing straight to the server's own node-builds folder and database — no
         browser upload, so no IIS request-size limit to hit. A no-op if this exact version/platform
         is already registered. Skippable with -SkipNodeBuildRegistration (implied by -SkipNodeBuild).
      9. Stops the IIS app pool
     10. Applies any pending EF Core migrations
     11. Copies published files to the IIS site, preserving setup-generated.json,
         appsettings.Production.json, data-protection-keys\, and every recording/spool/export
         directory
     12. Starts the IIS app pool (always, even on failure)
     13. Probes /health once the pool is back up

    This script deploys LarisVMS.Web to IIS and (by default) refreshes the node install package
    alongside it — recorder nodes are still separate Windows Services installed with
    install-node.ps1, never part of the IIS site, and must never be inside $DestinationPath.

    Must be run as Administrator (required for IIS management).

.EXAMPLE
    .\deploy.ps1 -IISSiteName "LarisVMS"
    .\deploy.ps1 -IISSiteName "LarisVMS" -SkipMigrations
    .\deploy.ps1 -IISSiteUrl "https://larisvms.example.com"
    .\deploy.ps1 -IISSiteName "LarisVMS" -ExtraNodePublishPath '\\files1\Install\LarisVMS\Node\win'
#>

param(
    [string]$WebProject           = (Join-Path $PSScriptRoot 'src\LarisVMS.Web\LarisVMS.Web.csproj'),
    [string]$MigrationsProject    = (Join-Path $PSScriptRoot 'src\LarisVMS.Infrastructure\LarisVMS.Infrastructure.csproj'),
    [string]$PublishDir           = (Join-Path $PSScriptRoot 'publish\LarisVMS.Web'),
    [string]$Configuration        = 'Release',
    [string]$DestinationPath      = 'E:\Sites\LarisVMS', # will be overridden if IIS site or URL is specified
    [string]$IISAppPoolName       = 'LarisVMS',
    [string]$IISSiteName          = 'LarisVMS',
    [string]$IISSiteUrl           = '',
    [string]$ConnectionString     = '',
    # Mirrors the built node package here too (e.g. a network share a recorder machine reads
    # directly) — passed straight through to build-node.ps1's own -ExtraPublishPath. Left blank
    # by default since this is inherently environment-specific, not something to hardcode for
    # every clone of this repo.
    [string]$ExtraNodePublishPath = '',
    [string]$NodeCsprojPath       = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj'),
    # The node package now bundles every ONNX Runtime backend and picks one at runtime per machine
    # (see build-node.ps1) — there is no accelerator to choose at build time. -SkipNodeVision still
    # builds a recording-only package with no AI detection at all.
    [switch]$SkipNodeVision,
    [switch]$SkipMigrations,
    [switch]$SkipNodeBuild,
    [switch]$SkipNodeBuildRegistration,
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

function Get-SqlClientConnectionString([string]$ConnectionString) {
    # System.Data.SqlClient (the legacy provider, used here rather than Microsoft.Data.SqlClient so
    # this script has no extra assembly to load) rejects keywords Microsoft.Data.SqlClient writes
    # into setup-generated.json — including "Trust Server Certificate" *with the spaces
    # SqlConnectionStringBuilder itself emits*, which a whitespace-sensitive regex match misses.
    # Comparing the whitespace-stripped key is what actually catches it.
    $forbiddenKeys = @('encrypt', 'trustservercertificate', 'multipleactiveresultsets', 'applicationintent')
    return ($ConnectionString -split ';' | Where-Object {
        $key = ($_ -split '=', 2)[0] -replace '\s', ''
        $forbiddenKeys -notcontains $key.ToLowerInvariant()
    }) -join ';'
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

# Resolved unconditionally (not just when running migrations): the node-build-registration step
# below needs it too, independent of -SkipMigrations, since registering a build is a database write
# of its own, unrelated to whether EF migrations happen to run this deploy.
if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
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
        Write-Host "Migrations (and node-build registration) will be skipped. Run the Setup wizard first, or pass -ConnectionString explicitly."
        $runMigrations = $false
    } else {
        Write-Ok "Connection string loaded from setup-generated.json."
    }
}

# ── storage root guard ────────────────────────────────────────────────────────
# robocopy /MIR below mirrors the publish output onto $DestinationPath — anything present at the
# destination that isn't in the publish output is deleted. If any node's storage (or archive) root
# were configured to a path under the IIS site directory, this would silently delete every recording
# under it on the next deploy. Storage config is per-node (Nodes.StorageRootPath / ArchiveRootPath),
# so this queries every node's paths straight from the database using the same connection string
# already resolved above for migrations. Best-effort only — if the query fails for any reason
# (pre-setup, migrations skipped, no -ConnectionString) the guard is skipped with a warning rather
# than blocking the deploy, and is not a substitute for keeping recordings outside the site dir.
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

        $normalizedDest = [System.IO.Path]::GetFullPath($DestinationPath).TrimEnd('\') + '\'
        foreach ($p in $nodePaths) {
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            try { $normalizedStorage = [System.IO.Path]::GetFullPath($p).TrimEnd('\') + '\' }
            catch { continue }  # a UNC path this deploy host can't resolve — not under the local site dir
            if ($normalizedStorage.StartsWith($normalizedDest, [StringComparison]::OrdinalIgnoreCase)) {
                throw "A recorder node's storage/archive path '$p' resolves under the IIS site directory " +
                      "'$DestinationPath'. A robocopy /MIR deploy would delete every recording under it. " +
                      "Move that node's path (Admin -> Nodes) outside the site directory before deploying."
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

Write-Step "Building and publishing ($Configuration)"

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Invoke-Cmd 'dotnet' @('publish', $WebProject, '-c', $Configuration, '-o', $PublishDir)
Write-Ok "Published to: $PublishDir"

# ── build recorder node package ───────────────────────────────────────────────
# Keeps publish\LarisVMS.Node\win (and -ExtraNodePublishPath, if given) current with every web
# deploy rather than depending on someone remembering to run build-node.ps1 separately. Failing
# here aborts before the app pool is touched, same as any other build failure above.

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
    # No $LASTEXITCODE check needed here: build-node.ps1 (Set-StrictMode + $ErrorActionPreference
    # = 'Stop') already throws on every failure path it has, including a non-zero exit from the
    # native commands it runs itself — that propagates as a terminating error through this call on
    # its own. Checking $LASTEXITCODE again here would actually be wrong: it's a session-wide
    # variable, so after a *successful* call it could still hold a leftover non-zero value from,
    # say, robocopy's own "some files copied" code (1) inside build-node.ps1, which build-node.ps1
    # itself correctly treats as success (only >= 8 is a real robocopy failure) — re-checking it
    # here would misreport that as this step having failed.
    & $buildNodeScript @buildNodeArgs
    Write-Ok "Node package built."
} else {
    Write-Host "Node package build skipped (-SkipNodeBuild)."
}

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

    # ── register node build for approval ────────────────────────────────────
    # Replaces the old browser-upload flow on Admin/NodeBuilds: uploading a several-hundred-MB
    # self-contained exe through the browser hit IIS's own requestFiltering maxAllowedContentLength
    # — a 413 raised before the request ever reached ASP.NET Core, since that limit is enforced
    # ahead of Kestrel/the app itself, not something RequestSizeLimit/RequestFormLimits on the page
    # could reach. This script already runs locally on the same server as LarisVMS.Web
    # (Administrator, IIS management), so instead it writes the file straight into
    # NodeBuildService's own storage folder and inserts the database row directly, the same way the
    # storage-root guard above already reads Settings straight from the database rather than going
    # through the app. The row lands as Pending — no node is ever offered it until an admin
    # approves it on Admin/NodeBuilds.
    #
    # Deliberately placed *after* migrations, not right after the node package build above: the
    # first deploy to ever add a new NodeBuildVersions column (as this feature's own migration did)
    # needs that column to actually exist before this INSERT runs — confirmed live as exactly this
    # failure the first time this shipped, when it ran before the migrations step and the INSERT
    # silently failed against a database that hadn't been migrated yet, leaving an orphaned file on
    # disk with no matching row and a warning that scrolled past unnoticed.
    if ($SkipNodeBuild -or $SkipNodeBuildRegistration) {
        Write-Host "`nNode-build registration skipped."
    } elseif ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        Write-Host "`nNo connection string available - skipping node-build registration."
    } else {
        Write-Step "Registering node build for approval"
        try {
            # build-node.ps1 writes the exact 4-part version it published (semver + monotonic build
            # number, e.g. 0.188.0.244) here, so a rebuild that didn't change the hand-maintained
            # semver still registers as a distinct, newer NodeBuildVersion. Fall back to the csproj's
            # 3-part <Version> only if that file is missing (an older build flow).
            $nodeVersionFile = Join-Path $PSScriptRoot 'publish\LarisVMS.Node\node-build-version.txt'
            if (Test-Path $nodeVersionFile) {
                $nodeVersion = (Get-Content $nodeVersionFile -Raw).Trim()
            } else {
                $nodeVersionMatch = Select-String -Path $NodeCsprojPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
                if (-not $nodeVersionMatch) { throw "Could not find <Version> in '$NodeCsprojPath'." }
                $nodeVersion = $nodeVersionMatch.Matches[0].Groups[1].Value
            }
            $platform = 'win-x64'
            $nodeExePath = Join-Path $PSScriptRoot 'publish\LarisVMS.Node\win\LarisVMS.Node.exe'
            if (-not (Test-Path $nodeExePath)) { throw "Built node exe not found: $nodeExePath" }

            # Optional — a package built with -SkipNodeVision (or an older node package still sitting
            # in publish\ from before this existed) simply has none, and every node's auto-update
            # already treats "no Vision binary on this build's row" as "nothing to offer" rather than
            # an error. See LarisVMS.Vision.Service.csproj's own comment for why this rides the same
            # Version/row as the Node exe instead of its own independent version.
            $visionExePath = Join-Path $PSScriptRoot 'publish\LarisVMS.Node\win\LarisVMS.Vision.Service.exe'
            $hasVision = Test-Path $visionExePath

            Add-Type -AssemblyName System.Data
            $sqlCs = Get-SqlClientConnectionString $ConnectionString
            $conn = New-Object System.Data.SqlClient.SqlConnection($sqlCs)
            $conn.Open()
            try {
                $checkCmd = $conn.CreateCommand()
                $checkCmd.CommandText = 'SELECT COUNT(*) FROM NodeBuildVersions WHERE Version = @Version AND Platform = @Platform'
                $checkCmd.Parameters.AddWithValue('@Version', $nodeVersion) | Out-Null
                $checkCmd.Parameters.AddWithValue('@Platform', $platform) | Out-Null
                $existingCount = [int]$checkCmd.ExecuteScalar()

                # Idempotent across repeated deploys that don't bump LarisVMS.Node's own <Version> —
                # a redeploy of the web tier alone (the common case) would otherwise queue up an
                # identical "new" Pending build to approve every single time.
                if ($existingCount -gt 0) {
                    Write-Host "Node build $nodeVersion ($platform) is already registered - skipping."
                } else {
                    # Mirrors NodeBuildService.DefaultRoot exactly — see that property's doc comment
                    # for why this has to live outside the IIS site directory.
                    $nodeBuildsRoot = Join-Path $env:ProgramData 'LarisVMS\node-builds'
                    New-Item -ItemType Directory -Path $nodeBuildsRoot -Force | Out-Null
                    $buildId = [guid]::NewGuid()
                    $storedPath = Join-Path $nodeBuildsRoot "$buildId.exe"
                    Copy-Item $nodeExePath $storedPath -Force

                    $hash = (Get-FileHash -Path $storedPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    $sizeBytes = (Get-Item $storedPath).Length

                    # Same file, alongside the Node exe, under its own name in the same builds folder —
                    # NULL columns (not an empty string) when this build has no Vision Service binary,
                    # so a node's own auto-update can tell "nothing to offer" apart from "offer this".
                    $visionStoredPath = $null
                    $visionHash = $null
                    $visionSizeBytes = $null
                    if ($hasVision) {
                        $visionStoredPath = Join-Path $nodeBuildsRoot "$buildId.vision.exe"
                        Copy-Item $visionExePath $visionStoredPath -Force
                        $visionHash = (Get-FileHash -Path $visionStoredPath -Algorithm SHA256).Hash.ToLowerInvariant()
                        $visionSizeBytes = (Get-Item $visionStoredPath).Length
                    }

                    $insertCmd = $conn.CreateCommand()
                    $insertCmd.CommandText = @'
INSERT INTO NodeBuildVersions (Id, Version, Platform, FilePath, SizeBytes, Sha256, VisionFilePath, VisionSizeBytes, VisionSha256, UploadedAt, Notes, Status, ApprovedAt, ApprovedBy)
VALUES (@Id, @Version, @Platform, @FilePath, @SizeBytes, @Sha256, @VisionFilePath, @VisionSizeBytes, @VisionSha256, GETUTCDATE(), @Notes, 0, NULL, NULL)
'@
                    $insertCmd.Parameters.AddWithValue('@Id', $buildId) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@Version', $nodeVersion) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@Platform', $platform) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@FilePath', $storedPath) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@SizeBytes', $sizeBytes) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@Sha256', $hash) | Out-Null
                    # AddWithValue with a bare $null does not reliably become SQL NULL — pass
                    # [DBNull]::Value explicitly, the standard .NET pattern for this.
                    $insertCmd.Parameters.AddWithValue('@VisionFilePath', $(if ($visionStoredPath) { $visionStoredPath } else { [DBNull]::Value })) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@VisionSizeBytes', $(if ($null -ne $visionSizeBytes) { $visionSizeBytes } else { [DBNull]::Value })) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@VisionSha256', $(if ($visionHash) { $visionHash } else { [DBNull]::Value })) | Out-Null
                    $insertCmd.Parameters.AddWithValue('@Notes', "Registered by deploy.ps1 on $(Get-Date -Format 'yyyy-MM-dd HH:mm')") | Out-Null
                    $insertCmd.ExecuteNonQuery() | Out-Null

                    Write-Ok "Node build $nodeVersion ($platform) registered as Pending - approve it on Admin -> Node Builds.$(if ($hasVision) { ' (includes Vision Service)' })"
                }
            } finally {
                $conn.Close()
            }
        } catch {
            # Best-effort, same as the storage-root guard above: a DB/hash hiccup here shouldn't
            # abort an otherwise-good web-tier deploy. Worst case, register the build by hand later
            # or re-run.
            Write-Host "Could not register node build for approval: $_" -ForegroundColor Yellow
        }
    }

    # ── seed the CUDA provider library ─────────────────────────────────────
    # build-node.ps1 splits onnxruntime_providers_cuda.dll (~320 MB) out of the node package into
    # publish\LarisVMS.Node\cuda-provider\ because only NVIDIA nodes load it. Copy it into the
    # server's vision-native cache (VisionNativeDistributor.CacheDirectory — %ProgramData% so the IIS
    # /MIR below can't wipe it); a node that resolves the CUDA backend downloads it from there once.
    $cudaProviderSrc = Join-Path $PSScriptRoot 'publish\LarisVMS.Node\cuda-provider\onnxruntime_providers_cuda.dll'
    if (Test-Path $cudaProviderSrc) {
        try {
            $visionNativeRoot = Join-Path $env:ProgramData 'LarisVMS\vision-native'
            New-Item -ItemType Directory -Path $visionNativeRoot -Force | Out-Null
            $cudaProviderDest = Join-Path $visionNativeRoot 'onnxruntime_providers_cuda.dll'
            $srcHash = (Get-FileHash $cudaProviderSrc -Algorithm SHA256).Hash
            if ((Test-Path $cudaProviderDest) -and (Get-FileHash $cudaProviderDest -Algorithm SHA256).Hash -eq $srcHash) {
                Write-Host "CUDA provider library already current on the server - skipping."
            } else {
                Write-Step "Seeding the CUDA provider library for node download"
                Copy-Item $cudaProviderSrc $cudaProviderDest -Force
                Write-Ok "Seeded to $cudaProviderDest"
            }
        } catch {
            Write-Host "Could not seed the CUDA provider library (NVIDIA nodes will keep running DirectML): $_" -ForegroundColor Yellow
        }
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
    #   - logs: FileLoggerProvider writes daily-rolling app-*.log files here, and they live inside
    #     the site directory because that's the one place the app pool identity is already known to
    #     be able to write (alongside IIS's own stdout_*.log). Without this exclusion every deploy
    #     mirrored them away, so Admin > System Logs only ever showed the current day no matter what
    #     the retention sweep was set to — the deploy, not retention, was deleting the history.
    $rcArgs = @(
        $PublishDir, $DestinationPath,
        '/MIR',
        '/XF', 'setup-generated.json', 'appsettings.Production.json',
        '/XD', 'data-protection-keys', 'recordings', 'spool', 'exports', 'logs',
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
