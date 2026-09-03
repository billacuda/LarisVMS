<#
.SYNOPSIS
    Shows exactly which smart-event codes a Dahua/Amcrest camera really emits, so the plugin's code
    table can be verified (or corrected) against actual firmware rather than documentation.

.DESCRIPTION
    The Dahua CGI plugin maps vendor event codes (SmartMotionHuman, SmartMotionVehicle, ...) onto
    person/vehicle/face detections. Those spellings vary across firmware generations, and the ONVIF
    spike already proved that assuming hardware behavior from documentation is how you get a wrong
    answer — so this attaches to the same endpoint the plugin uses and prints raw lines.

    Deliberately subscribes to [All], unlike the plugin itself: the point here is to discover what a
    camera sends, including codes the plugin doesn't know about yet.

    Read-only. Opens its own connection, entirely separate from anything the recorders are doing.

.PARAMETER CameraHost
    Camera address, e.g. 192.168.86.221. Deliberately not named -Host: $Host is a reserved
    PowerShell automatic variable (the console host object) and binding a parameter to it fails
    outright with "Cannot overwrite variable Host because it is read-only or constant."

.PARAMETER Credential
    Camera username/password. Prompted for if omitted, which keeps it out of shell history.

.PARAMETER Seconds
    How long to listen. Walk in front of the camera during this window.

.PARAMETER UsePluginCodes
    Subscribe with the exact filtered code list the plugin uses instead of [All]. Worth running once
    per firmware: [All] proves which codes a camera *can* send, but the plugin asks for a narrow list,
    and firmware that mishandles a long codes=[...] filter would go silent in production while an
    [All] probe still looked perfect.

.EXAMPLE
    .\probe-dahua-events.ps1 -CameraHost 192.168.86.221 -Seconds 60

.EXAMPLE
    .\probe-dahua-events.ps1 -CameraHost 192.168.86.221 -Seconds 60 -UsePluginCodes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias('Address', 'IP')][string]$CameraHost,
    [System.Management.Automation.PSCredential]$Credential = (Get-Credential -Message "Camera login"),
    [int]$Seconds = 60,
    [switch]$UsePluginCodes,
    [string]$Scheme = 'http'
)

$ErrorActionPreference = 'Stop'

# Kept in sync by hand with DahuaCgiEventParser's CodeMap. Serves double duty: the subscription list
# the plugin actually sends, and the "does the plugin know this code" check in the summary below.
$known = @(
    'SmartMotionHuman','HumanDetect','HumanTrait','SmartMotionVehicle','VehicleDetect',
    'TrafficJunction','FaceDetection','FaceRecognition',
    'AnimalDetection','SmartMotionAnimal','PetDetection',
    'LeftDetection','AbandonedObjectDetection','TakenAwayDetection','MissingObjectDetection',
    'CrossLineDetection','CrossRegionDetection'
)

$codes = if ($UsePluginCodes) { $known -join ',' } else { 'All' }
$uri = "{0}://{1}/cgi-bin/eventManager.cgi?action=attach&codes=[{2}]" -f $Scheme, $CameraHost, $codes
Write-Host "Attaching to $uri" -ForegroundColor Cyan
Write-Host "Listening for $Seconds seconds — walk through frame now." -ForegroundColor Yellow
Write-Host ""

# Digest auth is what these cameras require; HttpClientHandler negotiates it from Credentials.
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.Credentials = [System.Net.NetworkCredential]::new(
    $Credential.UserName, $Credential.GetNetworkCredential().Password)
$handler.PreAuthenticate = $true

$client = [System.Net.Http.HttpClient]::new($handler)
# The body never completes by design, so an overall timeout would abort a healthy stream.
$client.Timeout = [TimeSpan]::FromMilliseconds(-1)

$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$cts = [System.Threading.CancellationTokenSource]::new()
$cts.CancelAfter([TimeSpan]::FromSeconds($Seconds + 5))

try {
    $response = $client.GetAsync($uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cts.Token).
        GetAwaiter().GetResult()

    if (-not $response.IsSuccessStatusCode) {
        Write-Host "Camera returned $([int]$response.StatusCode) $($response.ReasonPhrase)." -ForegroundColor Red
        if ($response.StatusCode -eq 401) {
            Write-Host "Check the credentials, and that this account may read events." -ForegroundColor Red
        }
        exit 1
    }

    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $reader = [System.IO.StreamReader]::new($stream)

    while ((Get-Date) -lt $deadline) {
        $line = $reader.ReadLineAsync($cts.Token).GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        if ($line -notmatch '^Code=') { continue }

        Write-Host "  $line" -ForegroundColor Gray
        if ($line -match '^Code=([^;]+)') {
            $code = $Matches[1]
            $seen[$code] = 1 + ($seen[$code] ?? 0)
        }
    }
}
catch [OperationCanceledException] { }
catch {
    Write-Host "Connection failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    $cts.Dispose()
    $client.Dispose()
}

Write-Host ""
Write-Host "=== Codes this camera actually sent ===" -ForegroundColor Cyan
if ($seen.Count -eq 0) {
    if ($UsePluginCodes) {
        Write-Host "None — and this run used the plugin's filtered code list. If an [All] run on this" -ForegroundColor Red
        Write-Host "same camera DID show smart events, the firmware is rejecting the narrow" -ForegroundColor Red
        Write-Host "codes=[...] filter, and the plugin would be silent in production." -ForegroundColor Red
    } else {
        Write-Host "None. Either nothing happened in frame, or this camera doesn't publish smart events" -ForegroundColor Yellow
        Write-Host "over CGI. Re-run with real movement before concluding anything." -ForegroundColor Yellow
    }
    exit 0
}

foreach ($code in ($seen.Keys | Sort-Object)) {
    if ($known -contains $code) {
        Write-Host ("  {0,-28} x{1,-5} recognized by the plugin" -f $code, $seen[$code]) -ForegroundColor Green
    } else {
        Write-Host ("  {0,-28} x{1,-5} NOT mapped" -f $code, $seen[$code]) -ForegroundColor Yellow
    }
}

Write-Host ""
if ($UsePluginCodes) {
    Write-Host "Subscribed with the plugin's own filtered code list, so this is what the plugin would" -ForegroundColor Cyan
    Write-Host "really receive from this camera. Anything above confirms the filter is honored." -ForegroundColor Cyan
} else {
    Write-Host "Green codes already produce detections. Yellow ones are ignored today — if any of them" -ForegroundColor Cyan
    Write-Host "represent a person/vehicle/face, send them over and they're a one-line addition." -ForegroundColor Cyan
    Write-Host "Re-run with -UsePluginCodes to confirm the camera honors the plugin's narrow filter." -ForegroundColor Cyan
}
