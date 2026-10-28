# Shared release-version guard, dot-sourced by install-web.ps1 and build-installers.ps1.
#
# The release version is the first '## [X.Y.Z]' heading in CHANGELOG.md. Every shipped project's
# <Version> must match it, and so must AppRelease.Version (src\LarisVMS.Infrastructure\AppRelease.cs)
# — the in-app footer version is read from the AppVersions row the app writes from it at startup, not
# from any assembly, so a release that bumps the changelog and csprojs but forgets AppRelease never
# reports its version in the UI. (Up to 0.218.0 that row came from a BumpVersionX_Y_Z migration.)

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
    Throws unless every project's <Version> and AppRelease.Version match CHANGELOG.md.
    Returns the release version.
.PARAMETER Projects
    Hashtable of display name -> csproj path.
#>
function Assert-ReleaseVersionSync {
    param(
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][string]$AppReleasePath,
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

    $appRelease = Select-String -Path $AppReleasePath -Pattern 'const string Version = "([^"]+)"' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $appReleaseVersion = if ($appRelease) { $appRelease.Matches[0].Groups[1].Value } else { $null }
    if ($appReleaseVersion -ne $releaseVersion) {
        $mismatches += "  - AppRelease.Version: '$appReleaseVersion' in '$AppReleasePath' (expected $releaseVersion; " +
            "update its Notes too, or the in-app footer version stays on the previous release)"
    }

    if ($mismatches.Count -gt 0) {
        throw "This release is $releaseVersion (CHANGELOG.md) but the following are not in sync:`n" +
              "$($mismatches -join "`n")`n" +
              "Bump/add them for $releaseVersion first, or pass -SkipVersionSyncCheck if this is deliberate."
    }
    return $releaseVersion
}
