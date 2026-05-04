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

    # PostGIS is declared as a prerequisite in hartonomous.control's
    # `requires`, so CREATE EXTENSION hartonomous CASCADE will install
    # it automatically. This step is kept only as a fast-fail signal in
    # case the postgis extension files are missing from the container.
    Invoke-HartStep -Name 'Probe PostGIS availability' -Action {
        $available = Invoke-HartPsqlScalar -Cfg $Cfg -Database $Cfg.Postgres.MaintenanceDatabase `
            -Sql "SELECT count(*) FROM pg_available_extensions WHERE name = 'postgis'"
        if ([int]$available -lt 1) {
            throw "postgis extension is not available in this PG install — check the docker image."
        }
        Write-HartInfo 'PostGIS available (will auto-install via hartonomous CASCADE).'
    }

    # The hartonomous extension is the substrate. Installing it via
    # Bootstrap.ps1 creates: substrate + monitor schemas, all domains,
    # composite types, reference + core + junction tables (with LIST
    # partitions), reference seed data, native types (point4d/box4d/
    # geometry4d), C-bound functions (BLAKE3, traversal, glicko_bulk,
    # text_decompose, cp_*), substrate helper functions, views, opclasses
    # — atomically in one transaction. Same pattern as PostGIS / pgvector.

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Database ready (run Bootstrap.ps1 next to install hartonomous).'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
