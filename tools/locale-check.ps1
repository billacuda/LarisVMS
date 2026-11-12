# Checks a translation before it's uploaded or dropped into %ProgramData%\LarisVMS\locales, against
# the English: the interface text (a .json file) and, with -Help, a folder of translated Help pages.
# Works in Windows PowerShell 5.1 and PowerShell 7. Exit code 1 when something would show wrongly
# (a placeholder that doesn't match, a Help page whose headings don't line up) or a file can't be read.
#
#   .\tools\locale-check.ps1 fr.json
#   .\tools\locale-check.ps1 fr.json -English .\en.json     # en.json from Settings -> Language
#   .\tools\locale-check.ps1 fr.json -ListMissing           # every untranslated key, not just a count
#   .\tools\locale-check.ps1 -Help .\locales\fr\help        # translated Help pages only
#   .\tools\locale-check.ps1 fr.json -Help src\LarisVMS.Web\Help\fr
#
# Interface text: keys it's missing, keys this version doesn't use, and {placeholders} the English
# text doesn't have (those would show up as written).
# Help pages: pages not translated yet, files with a name LarisVMS doesn't use, pages whose headings
# don't match the English page's (links to their sections would land at the top of the page
# instead), and HTML (shown as plain text).

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Translation,
    [string]$English,
    [string]$Help,
    [string]$EnglishHelp,
    [switch]$ListMissing
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Translation -and -not $Help) {
    Write-Host 'Give a translation file (fr.json), a Help folder (-Help .\locales\fr\help), or both.' -ForegroundColor Red
    exit 1
}
# Resolved here rather than as the parameter defaults: Windows PowerShell 5.1 has no $PSScriptRoot there.
if (-not $English) { $English = Join-Path $PSScriptRoot '..\src\LarisVMS.Core\Localization\en.json' }
if (-not $EnglishHelp) { $EnglishHelp = Join-Path $PSScriptRoot '..\src\LarisVMS.Web\Help\en' }

$failed = $false

# The same flattening LarisVMS uses: nested objects join with dots, "_meta" is skipped, and only
# text values count.
function Read-LocaleFile([string]$Path) {
    if (-not (Test-Path $Path)) { throw "Can't find $Path." }
    try {
        $json = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        throw "$Path isn't valid JSON: $($_.Exception.Message)"
    }
    $entries = @{}
    function Add-Entries($node, [string]$prefix) {
        foreach ($prop in $node.PSObject.Properties) {
            $name = if ($prefix) { "$prefix.$($prop.Name)" } else { $prop.Name }
            if (-not $prefix -and $prop.Name -eq '_meta') { continue }
            if ($prop.Value -is [string]) {
                if ($prop.Value.Length -gt 0) { $entries[$name] = $prop.Value }
            } elseif ($prop.Value -is [System.Management.Automation.PSCustomObject]) {
                Add-Entries $prop.Value $name
            }
        }
    }
    Add-Entries $json ''
    return $entries
}

function Get-BaseKey([string]$Key) {
    return ($Key -replace '_(zero|one|two|few|many|other)$', '')
}

function Get-Placeholders([string]$Text) {
    return @([regex]::Matches($Text, '\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

# A Markdown page's headings as their levels ("1 2 2 3"), and whether it has HTML tags, both outside
# code blocks and inline code: what decides whether its section links work, and what shows as text.
function Read-HelpPage([string]$Path) {
    $levels = @()
    $html = $false
    $inFence = $false
    foreach ($line in (Get-Content $Path -Encoding UTF8)) {
        if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        if ($line -match '^(#{1,6})\s+\S') { $levels += $Matches[1].Length }
        $text = $line -replace '`[^`]*`', ''
        if ($text -match '<(/?)[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>') { $html = $true }
    }
    return [pscustomobject]@{ Levels = ($levels -join ' '); Html = $html }
}

function Test-Interface {
    try {
        $en = Read-LocaleFile $English
        $tr = Read-LocaleFile $Translation
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        return $false
    }

    $enBase = @{}
    foreach ($k in $en.Keys) { $enBase[(Get-BaseKey $k)] = $true }
    $trBase = @{}
    foreach ($k in $tr.Keys) { $trBase[(Get-BaseKey $k)] = $true }

    $missing = @($enBase.Keys | Where-Object { -not $trBase.ContainsKey($_) } | Sort-Object)
    $unused = @($tr.Keys | Where-Object { -not $enBase.ContainsKey((Get-BaseKey $_)) } | Sort-Object)

    $mismatched = @()
    foreach ($k in ($tr.Keys | Sort-Object)) {
        $source = if ($en.ContainsKey($k)) { $en[$k] } elseif ($en.ContainsKey("$(Get-BaseKey $k)_other")) { $en["$(Get-BaseKey $k)_other"] } else { $null }
        if ($null -eq $source) { continue }
        $allowed = @(Get-Placeholders $source) + 'count'
        $extra = @(Get-Placeholders $tr[$k] | Where-Object { $allowed -notcontains $_ })
        if ($extra.Count -gt 0) { $mismatched += "  $k uses {$($extra -join '}, {')}, which the English text doesn't have" }
        # Page-script text is plain text: LarisVMS drops one with markup or a double quote.
        if ($k.StartsWith('js.') -and $tr[$k] -match '[<>"]') { $mismatched += "  $k contains <, >, or a double quote, which page text can't have (use curly quotes); it shows in English" }
    }

    $covered = $enBase.Count - $missing.Count
    $percent = if ($enBase.Count) { [math]::Floor(100 * $covered / $enBase.Count) } else { 0 }
    Write-Host "$Translation`: $covered of $($enBase.Count) texts translated ($percent%)."

    if ($missing.Count) {
        Write-Host "$($missing.Count) not translated yet (they show in English)." -ForegroundColor Yellow
        if ($ListMissing) { $missing | ForEach-Object { Write-Host "  $_" } }
    }
    if ($unused.Count) {
        Write-Host "$($unused.Count) key(s) this version doesn't use (ignored):" -ForegroundColor Yellow
        $unused | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
        if ($unused.Count -gt 20) { Write-Host "  ..." }
    }
    if ($mismatched.Count) {
        Write-Host "$($mismatched.Count) placeholder problem(s):" -ForegroundColor Red
        $mismatched | ForEach-Object { Write-Host $_ }
        return $false
    }
    if (-not $missing.Count -and -not $unused.Count) { Write-Host 'Everything matches.' -ForegroundColor Green }
    return $true
}

function Test-Help {
    foreach ($dir in @($EnglishHelp, $Help)) {
        if (-not (Test-Path $dir -PathType Container)) {
            Write-Host "Can't find the folder $dir." -ForegroundColor Red
            return $false
        }
    }

    $enPages = @{}
    foreach ($f in (Get-ChildItem $EnglishHelp -Filter *.md -File)) { $enPages[$f.BaseName.ToLowerInvariant()] = $f.FullName }
    $trPages = @{}
    foreach ($f in (Get-ChildItem $Help -Filter *.md -File)) { $trPages[$f.BaseName.ToLowerInvariant()] = $f }

    $missing = @($enPages.Keys | Where-Object { -not $trPages.ContainsKey($_) } | Sort-Object)
    $unused = @($trPages.Keys | Where-Object { -not $enPages.ContainsKey($_) } | Sort-Object | ForEach-Object { $trPages[$_].Name })

    $headingProblems = @()
    $htmlPages = @()
    foreach ($name in ($trPages.Keys | Where-Object { $enPages.ContainsKey($_) } | Sort-Object)) {
        $file = $trPages[$name]
        $page = Read-HelpPage $file.FullName
        $source = Read-HelpPage $enPages[$name]
        if ($page.Levels -ne $source.Levels) {
            $count = if ($page.Levels) { @($page.Levels -split ' ').Count } else { 0 }
            $sourceCount = if ($source.Levels) { @($source.Levels -split ' ').Count } else { 0 }
            $headingProblems += "  $($file.Name): $count heading(s), the English page has $sourceCount (or they're at different levels)"
        }
        if ($page.Html) { $htmlPages += "  $($file.Name)" }
    }

    $covered = $enPages.Count - $missing.Count
    Write-Host "$Help`: $covered of $($enPages.Count) Help pages translated."
    if ($missing.Count) {
        Write-Host "$($missing.Count) page(s) not translated yet (they show in English)." -ForegroundColor Yellow
        if ($ListMissing) { $missing | ForEach-Object { Write-Host "  $(Split-Path $enPages[$_] -Leaf)" } }
    }
    if ($unused.Count) {
        Write-Host "$($unused.Count) file(s) with a name LarisVMS doesn't use (ignored; keep the English file names):" -ForegroundColor Yellow
        $unused | ForEach-Object { Write-Host "  $_" }
    }
    if ($htmlPages.Count) {
        Write-Host "$($htmlPages.Count) page(s) with HTML, which is shown as plain text (use Markdown):" -ForegroundColor Yellow
        $htmlPages | ForEach-Object { Write-Host $_ }
    }
    if ($headingProblems.Count) {
        Write-Host "$($headingProblems.Count) page(s) whose headings don't line up with the English page, so links to their sections won't land (keep the same headings in the same order and at the same levels):" -ForegroundColor Red
        $headingProblems | ForEach-Object { Write-Host $_ }
        return $false
    }
    if (-not $missing.Count -and -not $unused.Count -and -not $htmlPages.Count) { Write-Host 'Every page matches.' -ForegroundColor Green }
    return $true
}

if ($Translation) {
    if (-not (Test-Interface)) { $failed = $true }
}
if ($Help) {
    if ($Translation) { Write-Host '' }
    if (-not (Test-Help)) { $failed = $true }
}
if ($failed) { exit 1 }
exit 0
