# Shared reader for the database settings in setup-generated.json, dot-sourced by install-web.ps1 and
# deploy.ps1. Mirrors LarisVMS.Infrastructure.Data.DatabaseConfig: the connection string is stored
# without its password, which sits DPAPI-protected (LocalMachine) in Database:ProtectedPassword, and
# Database:Provider says which server it is (missing = SqlServer).

function Read-LarisDatabaseConfig {
    <#
    .SYNOPSIS
        Returns @{ Provider; ConnectionString } from a setup-generated.json, with the password put back
        into the connection string, or $null when the file has no connection string.
    #>
    param([Parameter(Mandatory)][string]$Path)

    # Property lookups that tolerate a missing key: the callers run under Set-StrictMode, and a file
    # written before 0.219.0 has no Database section.
    function Get-JsonValue($node, [string[]]$keys) {
        foreach ($key in $keys) {
            if ($null -eq $node) { return $null }
            $prop = $node.PSObject.Properties[$key]
            $node = if ($prop) { $prop.Value } else { $null }
        }
        return $node
    }

    $json = Get-Content $Path -Raw | ConvertFrom-Json
    $cs = Get-JsonValue $json 'ConnectionStrings', 'DefaultConnection'
    if ([string]::IsNullOrWhiteSpace($cs)) { return $null }

    $providerValue = Get-JsonValue $json 'Database', 'Provider'
    $provider = if ($providerValue) { [string]$providerValue } else { 'SqlServer' }
    $protected = Get-JsonValue $json 'Database', 'ProtectedPassword'
    if (-not [string]::IsNullOrWhiteSpace($protected)) {
        try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop }
        catch { Add-Type -AssemblyName System.Security }
        $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
            [Convert]::FromBase64String($protected),
            [System.Text.Encoding]::UTF8.GetBytes('LarisVMS.DatabasePassword'),
            [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.ConnectionString = $cs
        $builder['Password'] = [System.Text.Encoding]::UTF8.GetString($bytes)
        $cs = $builder.ConnectionString
    }

    return @{ Provider = $provider; ConnectionString = $cs }
}
