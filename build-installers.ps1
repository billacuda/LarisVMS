<#
.SYNOPSIS
    Builds the LarisVMS Web, Node, and Proxy installers: MSIs for Windows, .deb and .rpm for Linux.

.DESCRIPTION
      1. Checks the release version is in sync (CHANGELOG.md, every csproj <Version>, and
         AppRelease.Version) — see tools\VersionGuard.ps1
      2. Builds the recorder node and media proxy for every platform in -Platforms (build-node.ps1,
         build-proxy.ps1), all with one build number, so every product and platform shares a version
      3. Publishes LarisVMS.Web self-contained for each platform and bundles every platform's
         node and proxy builds into its packages\ folder (packages\<kind>\<platform>\), with every
         platform's CUDA provider (packages\cuda-provider\<platform>\) — the web app registers the
         builds as Pending on startup and serves the CUDA providers to NVIDIA nodes, so one server
         updates and accelerates Windows and Linux nodes alike with nothing to download
      4. Builds the three MSIs with WiX v5 (restored from NuGet; no global install needed) and copies
         them to publish\installers\LarisVMS-{Web,Node,Proxy}-<version>-x64.msi (other languages
         from -Cultures get a -<culture> suffix)
      5. Builds larisvms-{web,node,proxy} .deb and .rpm packages with nFPM (build-linux-packages.ps1)
      6. Writes SHA256SUMS.txt beside them, to upload with the release

    With -SignThumbprint, the node, proxy, updater, and Vision Service executables are signed before
    they're bundled, and the MSIs after they're built.

    Needs the .NET 10 SDK, and nFPM for the Linux packages (-Platforms win-x64 skips them). Does not
    need administrator rights — nothing is installed.

.EXAMPLE
    .\build-installers.ps1
    .\build-installers.ps1 -SkipNodeVision
    .\build-installers.ps1 -SignThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
    .\build-installers.ps1 -Cultures en-US,fr-FR
    .\build-installers.ps1 -Platforms win-x64
#>
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir     = (Join-Path $PSScriptRoot 'publish\installers'),
    [switch]$SkipNodeVision,
    [switch]$SkipVersionSyncCheck,
    # Skips refusing to build when a NuGet package has a known High or Critical vulnerability.
    [switch]$SkipVulnerabilityCheck,
    # Optional code-signing certificate (CurrentUser\My or LocalMachine\My) for the executables and MSIs.
    [string]$SignThumbprint = '',
    [string]$TimestampUrl   = 'http://timestamp.digicert.com',
    # Installer languages: one MSI set per culture, each needing installer\Common\Strings.<culture>.wxl.
    # The first is the main download; untranslated text falls back to English.
    [string[]]$Cultures     = @('en-US'),
    # Which platforms to build. Every Web package bundles node and proxy builds for all of them.
    [ValidateSet('win-x64', 'linux-x64')][string[]]$Platforms = @('win-x64', 'linux-x64'),
    # nFPM, for the .deb and .rpm packages.
    [string]$Nfpm = 'nfpm',
    # Optional: the publisher's private key (tools\publisher-key.ps1), passed to build-node.ps1 and build-proxy.ps1.
    [string]$PublisherKeyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host $Message -ForegroundColor Green }
function Invoke-Cmd([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

$signtool = $null
if ($SignThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe not found - install the Windows SDK, or omit -SignThumbprint." }
}
function Invoke-Sign([string[]]$Files) {
    if (-not $signtool) { return }
    foreach ($file in $Files) {
        Invoke-Cmd $signtool @('sign', '/sha1', $SignThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $file)
    }
}

$webProject = Join-Path $PSScriptRoot 'src\LarisVMS.Web\LarisVMS.Web.csproj'
# Each platform's Web publish. The Windows one is what the Web MSI harvests, so the Linux one must
# not sit inside it.
$publishWebFor = @{
    'win-x64'   = (Join-Path $PSScriptRoot 'publish\LarisVMS.Web')
    'linux-x64' = (Join-Path $PSScriptRoot 'publish\LarisVMS.Web-linux')
}
# Where build-node.ps1 and build-proxy.ps1 leave each platform's build ($NodeProxyBuildOutputs), and
# Copy-NodeProxyPackages, which bundles them into a Web publish (shared with install-web.ps1).
. (Join-Path $PSScriptRoot 'tools\BuildPackages.ps1')
$buildOutputs = $NodeProxyBuildOutputs

# ── version ──────────────────────────────────────────────────────────────────
. (Join-Path $PSScriptRoot 'tools\VersionGuard.ps1')
$changelog = Join-Path $PSScriptRoot 'CHANGELOG.md'
if ($SkipVersionSyncCheck) {
    $version = Get-CsprojVersion $webProject
    Write-Host "Skipping version sync guard - using LarisVMS.Web's <Version> $version."
} else {
    Write-Step "Checking release version"
    $version = Assert-ReleaseVersionSync -ChangelogPath $changelog `
        -AppReleasePath (Join-Path $PSScriptRoot 'src\LarisVMS.Infrastructure\AppRelease.cs') -Projects @{
            'LarisVMS.Web'         = $webProject
            'LarisVMS.Node'        = (Join-Path $PSScriptRoot 'src\LarisVMS.Node\LarisVMS.Node.csproj')
            'LarisVMS.Proxy'       = (Join-Path $PSScriptRoot 'src\LarisVMS.Proxy\LarisVMS.Proxy.csproj')
            'LarisVMS.NodeUpdater' = (Join-Path $PSScriptRoot 'src\LarisVMS.NodeUpdater\LarisVMS.NodeUpdater.csproj')
        }
    Write-Ok "Release $version"
}

# ── known vulnerabilities ────────────────────────────────────────────────────
if (-not $SkipVulnerabilityCheck) {
    Write-Step "Checking packages for known vulnerabilities"
    $report = & dotnet list (Join-Path $PSScriptRoot 'LarisVMS.slnx') package --vulnerable --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Couldn't check packages for vulnerabilities:`n$report" }
    if ($report -match '(?m)^\s*>\s.*\b(High|Critical)\b') {
        Write-Host $report
        throw "A package has a known High or Critical vulnerability. Update it, or pass -SkipVulnerabilityCheck."
    }
    if ($report -match 'has the following vulnerable packages') {
        Write-Host $report
        Write-Warning "Some packages have known Low or Moderate vulnerabilities."
    }
    Write-Ok "No High or Critical package vulnerabilities"
}

# ── node and proxy packages ──────────────────────────────────────────────────
# One build number for the whole run: every product and platform ships as the same version.
$buildNumberPath = Join-Path $PSScriptRoot 'build-number.txt'
if (-not (Test-Path $buildNumberPath)) { throw "build-number.txt not found at $buildNumberPath." }
$buildNumber = [int]((Get-Content $buildNumberPath -Raw).Trim()) + 1
Set-Content -Path $buildNumberPath -Value $buildNumber -NoNewline
$fullVersion = "$version.$buildNumber"
Write-Ok "Build $fullVersion"

foreach ($platform in $Platforms) {
    Write-Step "Building recorder node package ($platform)"
    $nodeArgs = @{ Configuration = $Configuration; Platform = $platform; BuildNumber = $buildNumber }
    if ($SkipNodeVision) { $nodeArgs['SkipVision'] = $true }
    if ($PublisherKeyPath) { $nodeArgs['PublisherKeyPath'] = $PublisherKeyPath }
    & (Join-Path $PSScriptRoot 'build-node.ps1') @nodeArgs

    Write-Step "Building media proxy package ($platform)"
    $proxyArgs = @{ Configuration = $Configuration; Platform = $platform; BuildNumber = $buildNumber }
    if ($PublisherKeyPath) { $proxyArgs['PublisherKeyPath'] = $PublisherKeyPath }
    & (Join-Path $PSScriptRoot 'build-proxy.ps1') @proxyArgs
}

# Signed before they're bundled: the server hashes and signs exactly these files for auto-update,
# and the MSIs pick up the same copies.
if ($signtool) {
    Write-Step "Signing node and proxy executables"
    $executables = @(
        'publish\LarisVMS.Node\win\LarisVMS.Node.exe', 'publish\LarisVMS.Node\win\LarisVMS.NodeUpdater.exe',
        'publish\LarisVMS.Node\win\LarisVMS.Vision.Service.exe',
        'publish\LarisVMS.Proxy\win\LarisVMS.Proxy.exe', 'publish\LarisVMS.Proxy\win\LarisVMS.NodeUpdater.exe'
    ) | ForEach-Object { Join-Path $PSScriptRoot $_ } | Where-Object { Test-Path $_ }
    Invoke-Sign $executables
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── web, self-contained, with the packages bundled ───────────────────────────
foreach ($webPlatform in $Platforms) {
    $publishWeb = $publishWebFor[$webPlatform]
    Write-Step "Publishing LarisVMS.Web (self-contained $webPlatform)"
    if (Test-Path $publishWeb) { Remove-Item $publishWeb -Recurse -Force }
    Invoke-Cmd 'dotnet' @('publish', $webProject, '-c', $Configuration, '-r', $webPlatform, '--self-contained', '-o', $publishWeb)

    # packages\<kind>\<platform>\: every platform's node and proxy, read by BundledBuildRegistrar.
    Copy-NodeProxyPackages -RepoRoot $PSScriptRoot -PackagesDir (Join-Path $publishWeb 'packages') -Platforms $Platforms
    Write-Ok "Web ($webPlatform) published with every platform's node, proxy, and CUDA provider bundled."
}

# ── MSIs ─────────────────────────────────────────────────────────────────────
$built = @()
$buildMsis = $Platforms -contains 'win-x64'
foreach ($culture in $Cultures) {
    if (-not $buildMsis) { break }
    if (-not (Test-Path (Join-Path $PSScriptRoot "installer\Common\Strings.$culture.wxl"))) {
        throw "No installer text for $culture - add installer\Common\Strings.$culture.wxl (copy Strings.en-US.wxl)."
    }
}
$cultureGroups = ($Cultures | ForEach-Object { if ($_ -eq 'en-US') { $_ } else { "$_,en-US" } }) -join ';'
foreach ($product in $(if ($buildMsis) { @('Web', 'Node', 'Proxy') } else { @() })) {
    Write-Step "Building LarisVMS-$product MSI ($($Cultures -join ', '))"
    $proj = Join-Path $PSScriptRoot "installer\$product\LarisVMS.$product.Installer.wixproj"
    # ';' and ',' escaped, or MSBuild reads them as separating properties.
    Invoke-Cmd 'dotnet' @('build', $proj, '-c', $Configuration, "-p:ProductVersion=$version", "-p:Cultures=$($cultureGroups -replace ';', '%3B' -replace ',', '%2C')", '-nologo', '-v', 'minimal')
    foreach ($culture in $Cultures) {
        $msi = Join-Path $PSScriptRoot "installer\$product\bin\$Configuration\$culture\LarisVMS-$product.msi"
        $suffix = if ($culture -eq $Cultures[0]) { '' } else { "-$culture" }
        $target = Join-Path $OutputDir "LarisVMS-$product-$version-x64$suffix.msi"
        Copy-Item $msi $target -Force
        $built += $target
    }
}

if ($signtool -and $built.Count -gt 0) {
    Write-Step "Signing installers"
    Invoke-Sign $built
}

# ── .deb and .rpm ────────────────────────────────────────────────────────────
if ($Platforms -contains 'linux-x64') {
    Write-Step "Building the Linux packages (.deb and .rpm)"
    $built += & (Join-Path $PSScriptRoot 'build-linux-packages.ps1') -Version $fullVersion -Nfpm $Nfpm -OutputDir $OutputDir `
        -WebDir $publishWebFor['linux-x64'] `
        -NodeDir (Join-Path $PSScriptRoot $buildOutputs['linux-x64'].Node) `
        -ProxyDir (Join-Path $PSScriptRoot $buildOutputs['linux-x64'].Proxy)
}

# ── checksums ────────────────────────────────────────────────────────────────
# Upload with the release, so a download can be checked against something other than the server
# it came from: Get-FileHash <file> -Algorithm SHA256, or sha256sum -c SHA256SUMS.txt.
Write-Step "Writing SHA256SUMS.txt"
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
$built | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } |
    Set-Content -Path $sums -Encoding ascii

Write-Host ""
Write-Ok "Installers:"
$built | ForEach-Object { Write-Host "  $_  ($([math]::Round((Get-Item $_).Length / 1MB)) MB)" }
Write-Host "  $sums"
