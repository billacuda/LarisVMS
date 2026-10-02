# Shared release-version guard, dot-sourced by install-web.ps1 and build-installers.ps1.
#
# The release version is the first '## [X.Y.Z]' heading in CHANGELOG.md. Every shipped project's
# <Version> must match it, and a matching BumpVersionX_Y_Z migration must exist — the in-app footer
# version is read from the AppVersions table that migration seeds, not from any assembly, so a
# release that bumps the changelog and csprojs but forgets the migration never reports its version
# in the UI.

function Get-CsprojVersion([string]$Path) {
    $m = Select-String -Path $Path -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $m) { throw "Could not find <Version> in '$Path'." }
    return $m.Matches[0].Groups[1].Value.Trim()
}

function Get-ReleaseVersion([string]$ChangelogPath) {
    $m = Select-String -Path $ChangelogPath -Pattern '^## \[(\d+\.\d+\.\d+)\]' | Select-Object -First 1
    if (-not $m) { throw "Could not find a '## [X.Y.Z]' release heading at the top of '$ChangelogPath'." }
    return $m.Matches[0].Groups[1].Value
}

<#
.SYNOPSIS
    Throws unless every project's <Version> and the AppVersions migration match CHANGELOG.md.
    Returns the release version.
.PARAMETER Projects
    Hashtable of display name -> csproj path.
#>
function Assert-ReleaseVersionSync {
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][string]$MigrationsPath,
        [Parameter(Mandatory)][hashtable]$Projects
    )

    $releaseVersion = Get-ReleaseVersion $ChangelogPath
    $mismatches = @()
    foreach ($name in $Projects.Keys) {
        $version = Get-CsprojVersion $Projects[$name]
        if ($version -ne $releaseVersion) {
            $mismatches += "  - ${name}: <Version>$version</Version> in '$($Projects[$name])' (expected $releaseVersion)"
        }
    }

    $migrationName = "BumpVersion$($releaseVersion -replace '\.', '_')"
    $migrationExists = (Test-Path $MigrationsPath) -and
        (Get-ChildItem $MigrationsPath -Filter "*_$migrationName.cs" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.Designer.cs' } | Select-Object -First 1)
    if (-not $migrationExists) {
        $mismatches += "  - AppVersions migration: no '$migrationName' migration found under '$MigrationsPath' " +
            "(the in-app footer version would stay on the previous release)"
    }

    if ($mismatches.Count -gt 0) {
        throw "This release is $releaseVersion (CHANGELOG.md) but the following are not in sync:`n" +
              "$($mismatches -join "`n")`n" +
              "Bump/add them for $releaseVersion first, or pass -SkipVersionSyncCheck if this is deliberate."
    }
    return $releaseVersion
}
