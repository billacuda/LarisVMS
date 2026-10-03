<#
.SYNOPSIS
    Creates (or removes) the private network the fake cameras live on.

.DESCRIPTION
    Creates a Hyper-V internal switch (default "LarisDemo"). The host's vEthernet adapter gets
    10.77.0.1/24, plus one alias IP per camera in cameras.json (10.77.0.11, .12, ...). Each fake
    camera then has its own address that can't be reached from your LAN, and LarisVMS's Discover
    finds the fakes through this adapter.

    Needs an elevated PowerShell and Hyper-V enabled.

.EXAMPLE
    .\setup-network.ps1
    .\setup-network.ps1 -Remove
#>
param([switch]$Remove)

. "$PSScriptRoot\common.ps1"
Assert-Admin

$net = $DemoConfig.network
$adapterAlias = "vEthernet ($($net.switchName))"

if ($Remove) {
    Write-Step "Removing switch $($net.switchName)"
    if (Get-VMSwitch -Name $net.switchName -ErrorAction SilentlyContinue) {
        Remove-VMSwitch -Name $net.switchName -Force
        Write-Ok 'Removed.'
    } else { Write-Ok 'Not present.' }
    Get-NetFirewallRule -DisplayName 'LarisVMS demo (inbound)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    return
}

Write-Step "Creating internal switch $($net.switchName)"
if (-not (Get-VMSwitch -Name $net.switchName -ErrorAction SilentlyContinue)) {
    New-VMSwitch -Name $net.switchName -SwitchType Internal | Out-Null
}
# The adapter takes a moment to appear after the switch is created.
for ($i = 0; $i -lt 20 -and -not (Get-NetAdapter -Name $adapterAlias -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }

$wanted = @($net.hostIp) + @($DemoConfig.cameras | ForEach-Object { $_.ip })
$existing = @(Get-NetIPAddress -InterfaceAlias $adapterAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object IPAddress)

# Drop the automatic 169.254.x.x address so only the demo addresses remain.
Get-NetIPAddress -InterfaceAlias $adapterAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notin $wanted } |
    Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue

foreach ($ip in $wanted) {
    if ($ip -notin $existing) {
        # SkipAsSource on the camera aliases keeps Windows using 10.77.0.1 for outgoing traffic,
        # so LarisVMS's discovery probe goes out from the host address.
        $skip = $ip -ne $net.hostIp
        New-NetIPAddress -InterfaceAlias $adapterAlias -IPAddress $ip -PrefixLength $net.prefixLength -SkipAsSource $skip | Out-Null
    }
}

# Treat it as a private network, so the firewall's private-profile rules apply.
Set-NetConnectionProfile -InterfaceAlias $adapterAlias -NetworkCategory Private -ErrorAction SilentlyContinue

Write-Step 'Firewall rules for the demo subnet'
$subnet = "$($net.hostIp)/$($net.prefixLength)"
if (-not (Get-NetFirewallRule -DisplayName 'LarisVMS demo (inbound)' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'LarisVMS demo (inbound)' -Direction Inbound -Action Allow `
        -LocalAddress $subnet -RemoteAddress $subnet -Profile Any | Out-Null
}

Get-NetIPAddress -InterfaceAlias $adapterAlias -AddressFamily IPv4 | Sort-Object IPAddress | Format-Table IPAddress, PrefixLength, SkipAsSource
Write-Ok "Network ready on $adapterAlias."
