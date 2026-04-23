#requires -Version 7
<#
.SYNOPSIS
  Create the `hartonomous` database (no-op if it already exists) and enable
  PostGIS by re-running the docker-entrypoint init script.

.EXAMPLE
  pwsh scripts/db/Create.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Create' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }

    Invoke-HartStep -Name "CREATE DATABASE $($Cfg.Postgres.Database)" -Action {
        if (Test-HartDatabaseExists -Cfg $Cfg) {
            Write-HartInfo "$($Cfg.Postgres.Database) already exists."
        } else {
            Invoke-HartPsql -Cfg $Cfg `
                -Database $Cfg.Postgres.MaintenanceDatabase `
                -Sql "CREATE DATABASE $($Cfg.Postgres.Database) OWNER $($Cfg.Postgres.User)" | Out-Null
            Write-HartInfo "Created."
        }
    }

    Invoke-HartStep -Name 'Ensure PostGIS extension' -Action {
        if (-not (Test-HartPostgisEnabled -Cfg $Cfg)) {
            Invoke-HartPsql -Cfg $Cfg -Sql 'CREATE EXTENSION IF NOT EXISTS postgis' | Out-Null
        }
        Write-HartInfo 'PostGIS ready.'
    }

    Invoke-HartStep -Name 'Ensure hartonomous extension' -Action {
        Invoke-HartPsql -Cfg $Cfg -Sql 'CREATE EXTENSION IF NOT EXISTS hartonomous' | Out-Null
        Write-HartInfo 'hartonomous extension ready.'
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Database ready.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
