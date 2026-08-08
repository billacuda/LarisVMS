<#
.SYNOPSIS
    Placeholder for the recorder node installer (milestone M3).

.DESCRIPTION
    Once Rcordr.Node exists, this script will register the machine as a recorder node against the
    given Rcordr.Web server and install it as a Windows Service, mirroring dploid's
    install-agent.ps1. The Setup wizard's Node step already prints the intended command line:

        .\install-node.ps1 -ServerUrl "https://rcordr.example.com" -RegistrationKey "<key>"

    so the wizard's shape doesn't need to change once M3 lands.
#>

param(
    [Parameter(Mandatory)] [string]$ServerUrl,
    [Parameter(Mandatory)] [string]$RegistrationKey
)

throw "install-node.ps1 is not yet implemented — Rcordr.Node ships in milestone M3. See CHANGELOG.md."
