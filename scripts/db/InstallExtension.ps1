#requires -Version 7
<#
.SYNOPSIS
  `CREATE EXTENSION hartonomous` in the substrate DB (after build/PgExtension.ps1
  has compiled+installed the .so/.control/.sql inside the container).

.PARAMETER Drop
  First `DROP EXTENSION IF EXISTS hartonomous CASCADE` — use this when iterating
  on the extension's SQL surface.

.EXAMPLE
  pwsh scripts/db/InstallExtension.ps1
  pwsh scripts/db/InstallExtension.ps1 -Drop
#>
[CmdletBinding()]
param(
    [switch]$Drop
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.InstallExtension' -Cfg $Cfg

try {
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }

    if ($Drop) {
        Invoke-HartStep -Name 'DROP EXTENSION hartonomous CASCADE' -Action {
            Invoke-HartPsql -Cfg $Cfg -Sql 'DROP EXTENSION IF EXISTS hartonomous CASCADE' | Out-Null
        }
    }

    Invoke-HartStep -Name 'CREATE EXTENSION hartonomous' -Action {
        if (Test-HartHartonomousExtensionInstalled -Cfg $Cfg) {
            Write-HartInfo 'hartonomous extension already installed.'
        } else {
            Invoke-HartPsql -Cfg $Cfg -Sql 'CREATE EXTENSION hartonomous' | Out-Null
            Write-HartInfo 'Installed.'
        }
    }

    Invoke-HartStep -Name 'Verify version' -Action {
        $v = Invoke-HartPsqlScalar -Cfg $Cfg -Sql "SELECT hartonomous_version()" -ErrorAction SilentlyContinue
        if ($v) { Write-HartInfo "hartonomous_version() = $v" }
    } -ContinueOnError

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Extension ready.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
