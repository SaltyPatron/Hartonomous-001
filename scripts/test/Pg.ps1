#requires -Version 7
<#
.SYNOPSIS
  Run pg_regress against the hartonomous_pg extension.

.DESCRIPTION
  Uses the in-container pg_regress bundled with PostgreSQL 17. The extension
  must be built+installed first (see scripts/build/PgExtension.ps1).

.EXAMPLE
  pwsh scripts/test/Pg.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'test.Pg' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }

    Invoke-HartStep -Name 'pg_regress (installcheck)' -Action {
        $inline = 'cd /hartonomous_pg && make PG_CONFIG=/usr/bin/pg_config installcheck'
        Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv @('bash','-lc',$inline)
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'pg_regress passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
