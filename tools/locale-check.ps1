# Checks a translation file against the English text before it's uploaded or dropped into
# %ProgramData%\LarisVMS\locales: keys it's missing, keys this version doesn't use, and {placeholders}
# the English text doesn't have (those would show up as written). Works in Windows PowerShell 5.1
# and PowerShell 7. Exit code 1 when a placeholder doesn't match or a file can't be read.
#
#   .\tools\locale-check.ps1 fr.json
#   .\tools\locale-check.ps1 fr.json -English .\en.json     # en.json from Settings -> Language
#   .\tools\locale-check.ps1 fr.json -ListMissing           # every untranslated key, not just a count

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Translation,
    [string]$English,
    [switch]$ListMissing
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Resolved here rather than as the parameter default: Windows PowerShell 5.1 has no $PSScriptRoot there.
if (-not $English) { $English = Join-Path $PSScriptRoot '..\src\LarisVMS.Core\Localization\en.json' }

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

try {
    $en = Read-LocaleFile $English
    $tr = Read-LocaleFile $Translation
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
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
    exit 1
}
if (-not $missing.Count -and -not $unused.Count) { Write-Host 'Everything matches.' -ForegroundColor Green }
exit 0
