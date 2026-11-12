#Requires -Version 7.2
<#
.SYNOPSIS
    Makes the publisher's offline update-signing key, or signs a build with it.

.DESCRIPTION
    Nodes and proxies built with -PublisherKeyPath (build-node.ps1, build-proxy.ps1, deploy.ps1) carry
    the public key in src\LarisVMS.Core\Security\publisher-key.pem and install an update only if
    it's signed with the matching private key, on top of the server's own signature. Builds without
    -PublisherKeyPath are unsigned and carry no key, so it's opt-in.
    A compromised LarisVMS server then can't push its own code to nodes: it never holds this key.

    -New makes the key pair. The private key is written, encrypted with a password you choose, to
    -PrivateKeyPath. Keep that file and the password offline (a USB drive in a safe, a password
    manager), not on the LarisVMS server and not in the repository. The public key is written to
    src\LarisVMS.Core\Security\publisher-key.pem; commit it.

    -Sign signs one update payload (build-node.ps1 and build-proxy.ps1 call this) and prints the
    signature. The password comes from LARISVMS_PUBLISHER_KEY_PASSWORD if set, otherwise it's asked for.

    Losing the private key: make a new pair and ship a build with the new public key by hand
    (reinstall the nodes and proxies); they won't accept an automatic update signed by a new key.

.EXAMPLE
    .\tools\publisher-key.ps1 -New -PrivateKeyPath E:\keys\larisvms-publisher.pem

.EXAMPLE
    .\tools\publisher-key.ps1 -Sign -PrivateKeyPath E:\keys\larisvms-publisher.pem -Payload 'larisvms-update:v1|win-x64|...'
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory, ParameterSetName = 'New')][switch]$New,
    [Parameter(Mandatory, ParameterSetName = 'Sign')][switch]$Sign,
    [Parameter(Mandatory)][string]$PrivateKeyPath,
    [Parameter(Mandatory, ParameterSetName = 'Sign')][string]$Payload
)

$ErrorActionPreference = 'Stop'
$publicKeyPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\LarisVMS.Core\Security\publisher-key.pem'

function Read-Password([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    return [System.Net.NetworkCredential]::new('', $secure).Password
}

if ($New) {
    if (Test-Path $PrivateKeyPath) { throw "'$PrivateKeyPath' already exists. Choose another path; never overwrite a key in use." }
    if (Test-Path $publicKeyPath) { throw "'$publicKeyPath' already exists. Remove it first if you really mean to replace the publisher key." }
    $repo = (Resolve-Path (Split-Path $PSScriptRoot -Parent)).Path
    $target = [System.IO.Path]::GetFullPath($PrivateKeyPath)
    if ($target.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Keep the private key outside the repository ($repo)."
    }

    $password = Read-Password 'Password for the private key (at least 12 characters)'
    if ($password.Length -lt 12) { throw 'The password must be at least 12 characters.' }
    if ($password -cne (Read-Password 'Same password again')) { throw "The passwords don't match." }

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $pbe = [System.Security.Cryptography.PbeParameters]::new(
            [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
        $privatePem = $ecdsa.ExportEncryptedPkcs8PrivateKeyPem($password, $pbe)
        $publicPem = $ecdsa.ExportSubjectPublicKeyInfoPem()
    } finally {
        $ecdsa.Dispose()
    }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Set-Content -Path $target -Value $privatePem -NoNewline -Encoding ascii
    Set-Content -Path $publicKeyPath -Value $publicPem -NoNewline -Encoding ascii
    Write-Host "Private key (encrypted): $target" -ForegroundColor Green
    Write-Host "Public key:              $publicKeyPath" -ForegroundColor Green
    Write-Host 'Move the private key offline and commit the public key. Pass -PublisherKeyPath to sign a build.'
    return
}

$password = $env:LARISVMS_PUBLISHER_KEY_PASSWORD
if (-not $password) { $password = Read-Password 'Publisher key password' }
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromEncryptedPem((Get-Content -Path $PrivateKeyPath -Raw), $password)
    if (Test-Path $publicKeyPath) {
        $built = ((Get-Content $publicKeyPath -Raw) -replace '\s', '')
        $mine = ($ecdsa.ExportSubjectPublicKeyInfoPem() -replace '\s', '')
        if ($built -ne $mine) { throw "This private key doesn't match $publicKeyPath; nodes would refuse the build." }
    }
    $signature = $ecdsa.SignData([System.Text.Encoding]::UTF8.GetBytes($Payload),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    [Convert]::ToBase64String($signature)
} finally {
    $ecdsa.Dispose()
}
