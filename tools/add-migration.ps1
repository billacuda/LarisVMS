<#
.SYNOPSIS
    Adds an EF Core migration for both database servers: SQL Server and MariaDB (experimental).

.DESCRIPTION
    Every schema change needs a migration in each provider's migrations assembly:
      - SQL Server: src\LarisVMS.Infrastructure\Migrations
      - MariaDB:    src\LarisVMS.Infrastructure.MariaDb\Migrations
    Neither needs a database server. Hand-written migrationBuilder.Sql goes in the provider's own
    migration (or branches on migrationBuilder.IsSqlServer()). MariaDbModelTests fails while the
    MariaDB migrations are behind the model.

.EXAMPLE
    .\tools\add-migration.ps1 AddCameraNotes
#>
param(
    [Parameter(Mandatory)][string]$Name
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Push-Location $root
try {
    dotnet tool restore | Out-Null

    Write-Host "SQL Server migration '$Name'" -ForegroundColor Cyan
    dotnet ef migrations add $Name --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web
    if ($LASTEXITCODE -ne 0) { throw "SQL Server migration failed." }

    Write-Host "MariaDB migration '$Name'" -ForegroundColor Cyan
    dotnet ef migrations add $Name --project src\LarisVMS.Infrastructure.MariaDb --startup-project src\LarisVMS.Infrastructure.MariaDb --output-dir Migrations
    if ($LASTEXITCODE -ne 0) {
        throw "MariaDB migration failed. The SQL Server one was added; remove it with " +
              "'dotnet ef migrations remove --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web' or fix and add the MariaDB one by hand."
    }
}
finally {
    Pop-Location
}
