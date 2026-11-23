<#
.SYNOPSIS
    Packages the linux-x64 Web, Node, and Proxy builds as .deb and .rpm files with nFPM.

.DESCRIPTION
    Called by build-installers.ps1 after it has published the linux-x64 builds; can also be run on its
    own against existing publish folders. For each product it:
      1. writes the maintainer scripts from installer\linux\scripts (filling in which product, with
         Unix line endings),
      2. copies installer\linux\<product>\nfpm.yaml and appends every published file to its contents
         with an explicit mode (0755 for the programs, 0644 for the rest), since a build made on
         Windows carries no Unix modes, and
      3. runs nFPM once for .deb and once for .rpm.

    Needs nFPM (https://nfpm.goreleaser.com): a single binary for Windows, Linux, and macOS. Put it
    on PATH or pass -Nfpm. Runs the same on Windows and Linux (PowerShell 7).

    Output: larisvms-{web,node,proxy}_<version>-1_amd64.deb and larisvms-{web,node,proxy}-<version>-1.x86_64.rpm.

    The package version is the version the node and proxy were published as (their
    *-build-version.txt), so a package never claims a version its programs don't report; -Version,
    when given, must match it. Repackaging needs a fresh publish, not just a new -Version.

.EXAMPLE
    .\build-linux-packages.ps1
#>
param(
    # Defaults to the published builds' version; must match it when given.
    [string]$Version,
    # Beside, not inside, the Windows Web publish (the Web MSI harvests that one).
    [string]$WebDir     = (Join-Path $PSScriptRoot 'publish\LarisVMS.Web-linux'),
    [string]$NodeDir    = (Join-Path $PSScriptRoot 'publish\LarisVMS.Node\linux'),
    [string]$ProxyDir   = (Join-Path $PSScriptRoot 'publish\LarisVMS.Proxy\linux'),
    [string]$OutputDir  = (Join-Path $PSScriptRoot 'publish\installers'),
    [string]$Nfpm       = 'nfpm',
    [string]$Maintainer = 'LarisVMS <billacuda@users.noreply.github.com>',
    # Which products to package (each needs its publish folder).
    [ValidateSet('web', 'node', 'proxy')][string[]]$Products = @('web', 'node', 'proxy')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }

if (-not (Get-Command $Nfpm -ErrorAction SilentlyContinue)) {
    throw "nFPM not found ('$Nfpm'). Install it from https://nfpm.goreleaser.com/install/ or pass -Nfpm <path>."
}

# The version the programs report is the one baked into them at publish time (build-node.ps1 and
# build-proxy.ps1 write it beside their output). The package must carry the same one.
$published = @{}
foreach ($pair in @(@{ Kind = 'node'; Dir = $NodeDir; File = 'node-build-version.txt' },
                    @{ Kind = 'proxy'; Dir = $ProxyDir; File = 'proxy-build-version.txt' })) {
    if ($Products -notcontains $pair.Kind -and -not ($Products -contains 'web')) { continue }
    $versionFile = Join-Path (Split-Path $pair.Dir -Parent) "linux-x64-meta\$($pair.File)"
    if (Test-Path $versionFile) { $published[$pair.Kind] = (Get-Content $versionFile -Raw).Trim() }
}
$distinct = @($published.Values | Select-Object -Unique)
if ($distinct.Count -gt 1) {
    throw "The published node and proxy are different builds ($(($published.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')). Rebuild them together with build-installers.ps1."
}
if (-not $Version) {
    if ($distinct.Count -eq 0) { throw "No published build version found (linux-x64-meta\*-build-version.txt); pass -Version or publish first." }
    $Version = $distinct[0]
} elseif ($distinct.Count -eq 1 -and $distinct[0] -ne $Version) {
    throw "-Version $Version doesn't match the published programs, which are build $($distinct[0]). " +
        "They'd report $($distinct[0]) once installed. Publish again (build-installers.ps1) to package a new build."
}

$linuxRoot = Join-Path $PSScriptRoot 'installer\linux'
$workRoot = Join-Path $PSScriptRoot 'publish\linux-packaging'
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workRoot, $OutputDir -Force | Out-Null

# The programs; everything else is a library or data file.
$executables = @('LarisVMS.Web', 'LarisVMS.Node', 'LarisVMS.NodeUpdater', 'LarisVMS.Vision.Service', 'LarisVMS.Proxy', 'createdump')
# Never shipped: debug symbols, Windows-only files, and operator-owned config (it lives in /etc/larisvms).
$excluded = @('*.pdb', '*.exe', '*.dll.config', 'web.config', 'install-node.ps1', 'install-proxy.ps1',
    'appsettings.Development.json', 'appsettings.Production.json')

$sources = @{ web = $WebDir; node = $NodeDir; proxy = $ProxyDir }

function ConvertTo-UnixPath([string]$path) { $path.Replace('\', '/') }

# Single-quoted YAML string.
function ConvertTo-YamlString([string]$value) { "'" + $value.Replace("'", "''") + "'" }

$built = @()
foreach ($kind in $Products) {
    $source = $sources[$kind]
    if (-not (Test-Path $source)) { throw "No $kind build at $source. Publish it for linux-x64 first (build-installers.ps1 -Platforms linux-x64)." }
    $source = (Resolve-Path $source).Path

    Write-Step "Packaging larisvms-$kind $Version"
    $work = Join-Path $workRoot $kind
    $scripts = Join-Path $work 'scripts'
    New-Item -ItemType Directory -Path $scripts -Force | Out-Null

    # Maintainer scripts, with this product filled in and Unix line endings (a CR breaks /bin/sh).
    $templates = @{
        'preinstall.sh'  = 'preinstall.sh'
        'postinstall.sh' = 'postinstall.sh.in'
        'preremove.sh'   = 'preremove.sh.in'
        'postremove.sh'  = 'postremove.sh.in'
    }
    foreach ($name in $templates.Keys) {
        $text = (Get-Content (Join-Path $linuxRoot "scripts\$($templates[$name])") -Raw).Replace("`r`n", "`n").Replace('@KIND@', $kind)
        [System.IO.File]::WriteAllText((Join-Path $scripts $name), $text, (New-Object System.Text.UTF8Encoding $false))
    }

    # Units and env templates, also with Unix line endings.
    $unitDir = Join-Path $work 'linux'
    New-Item -ItemType Directory -Path (Join-Path $unitDir $kind) -Force | Out-Null
    foreach ($file in Get-ChildItem (Join-Path $linuxRoot $kind) -File | Where-Object Name -ne 'nfpm.yaml') {
        $text = (Get-Content $file.FullName -Raw).Replace("`r`n", "`n")
        [System.IO.File]::WriteAllText((Join-Path (Join-Path $unitDir $kind) $file.Name), $text, (New-Object System.Text.UTF8Encoding $false))
    }

    # The package definition, with every published file appended to contents.
    $yaml = New-Object System.Text.StringBuilder
    # nFPM only expands environment variables in a few fields (not contents' src), so the
    # ${LARISVMS_*} placeholders are filled in here.
    $placeholders = @{
        '${LARISVMS_VERSION}'    = $Version
        '${LARISVMS_MAINTAINER}' = $Maintainer
        '${LARISVMS_SCRIPTS}'    = ConvertTo-UnixPath $scripts
        '${LARISVMS_LINUX}'      = ConvertTo-UnixPath $unitDir
    }
    $template = (Get-Content (Join-Path $linuxRoot "$kind\nfpm.yaml") -Raw).Replace("`r`n", "`n").TrimEnd()
    foreach ($name in $placeholders.Keys) { $template = $template.Replace($name, $placeholders[$name]) }
    [void]$yaml.Append($template)
    [void]$yaml.Append("`n")
    $count = 0
    foreach ($file in Get-ChildItem $source -Recurse -File -Force) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
        # packages\ is the node and proxy builds the server hands out, Windows .exe files included:
        # copied as they are.
        $isBundledBuild = $kind -eq 'web' -and $relative -match '^packages[\\/]'
        if (-not $isBundledBuild -and ($excluded | Where-Object { $file.Name -like $_ })) { continue }
        $mode = if ($executables -contains $file.Name) { '0755' } else { '0644' }
        [void]$yaml.Append("  - src: $(ConvertTo-YamlString (ConvertTo-UnixPath $file.FullName))`n")
        [void]$yaml.Append("    dst: $(ConvertTo-YamlString "/opt/larisvms/$kind/$(ConvertTo-UnixPath $relative)")`n")
        [void]$yaml.Append("    file_info:`n      mode: $mode`n")
        $count++
    }
    $config = Join-Path $work 'nfpm.yaml'
    [System.IO.File]::WriteAllText($config, $yaml.ToString(), (New-Object System.Text.UTF8Encoding $false))
    Write-Ok "$count program files"

    # nFPM's own file names for each format.
    $targets = @{
        deb = "larisvms-${kind}_$Version-1_amd64.deb"
        rpm = "larisvms-$kind-$Version-1.x86_64.rpm"
    }
    foreach ($packager in @('deb', 'rpm')) {
        $target = Join-Path $OutputDir $targets[$packager]
        Remove-Item $target -ErrorAction SilentlyContinue
        # Out-Host: nFPM's progress lines would otherwise join $built in the caller's pipeline.
        & $Nfpm package --config $config --packager $packager --target $target | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "nFPM failed packaging larisvms-$kind as .$packager (exit $LASTEXITCODE)." }
        if (-not (Test-Path $target)) { throw "nFPM reported success but $target wasn't written." }
        Write-Ok $targets[$packager]
        $built += $target
    }
}

Remove-Item $workRoot -Recurse -Force

# The caller (build-installers.ps1) adds these to SHA256SUMS.txt.
$built
