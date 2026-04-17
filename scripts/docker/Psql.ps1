#requires -Version 7
<#
.SYNOPSIS
  Open an interactive psql shell inside the Postgres container.

.PARAMETER Database
  Database to connect to. Default: the configured substrate DB.

.PARAMETER Sql
  If provided, runs a one-shot query and returns (non-interactive).

.EXAMPLE
  pwsh scripts/docker/Psql.ps1
  pwsh scripts/docker/Psql.ps1 -Database postgres
  pwsh scripts/docker/Psql.ps1 -Sql "SELECT COUNT(*) FROM substrate.entity"
#>
[CmdletBinding()]
param(
    [string]$Database,
    [string]$Sql
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Psql' -Cfg $Cfg

if (-not $Database) { $Database = $Cfg.Postgres.Database }

try {
    Assert-HartCommand -Name 'docker' | Out-Null

    if ($Sql) {
        $out = Invoke-HartPsql -Cfg $Cfg -Sql $Sql -Database $Database
        $out | Write-Output
        Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
    }

    # Interactive shell — hand off to docker exec -it.
    Write-HartInfo "Attaching to psql in $($Cfg.Docker.PgContainer) [db=$Database]. Ctrl-D to exit."
    & docker exec -it $Cfg.Docker.PgContainer psql -U $Cfg.Postgres.User -d $Database
    Exit-Hartonomous -Code $LASTEXITCODE
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
