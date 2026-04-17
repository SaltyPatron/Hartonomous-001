#requires -Version 7
<#
.SYNOPSIS
  Drop the hartonomous database. Destructive — requires confirmation.

.PARAMETER Force
  Skip the interactive confirmation prompt.

.EXAMPLE
  pwsh scripts/db/Drop.ps1 -Force
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [switch]$Force
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Drop' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }
    if (-not (Test-HartDatabaseExists -Cfg $Cfg)) {
        Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "$($Cfg.Postgres.Database) does not exist — nothing to drop."
    }
    if (-not $Force -and -not $PSCmdlet.ShouldProcess($Cfg.Postgres.Database, 'DROP DATABASE')) {
        Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Aborted.'
    }

    Invoke-HartStep -Name "Terminate connections + DROP DATABASE $($Cfg.Postgres.Database)" -Action {
        $sqlTerm = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$($Cfg.Postgres.Database)' AND pid<>pg_backend_pid()"
        Invoke-HartPsql -Cfg $Cfg -Database $Cfg.Postgres.MaintenanceDatabase -Sql $sqlTerm | Out-Null
        Invoke-HartPsql -Cfg $Cfg -Database $Cfg.Postgres.MaintenanceDatabase -Sql "DROP DATABASE IF EXISTS $($Cfg.Postgres.Database)" | Out-Null
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Database dropped.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
