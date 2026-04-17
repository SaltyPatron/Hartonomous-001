#requires -Version 7
<#
.SYNOPSIS
  Drive the Hartonomous.Cli migration runner.

.PARAMETER Action
  up | down | status.

.PARAMETER Target
  Version to roll down to (only with -Action down).

.PARAMETER NoBuild
  Skip the implicit dotnet build before running the CLI.

.EXAMPLE
  pwsh scripts/db/Migrate.ps1 -Action status
  pwsh scripts/db/Migrate.ps1 -Action up
  pwsh scripts/db/Migrate.ps1 -Action down -Target 20
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('up','down','status')] [string]$Action,
    [int]$Target,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName "db.Migrate.$Action" -Cfg $Cfg

try {
    Invoke-HartStep -Name "migrate $Action" -Action {
        if ($Action -eq 'down' -and $PSBoundParameters.ContainsKey('Target')) {
            Invoke-HartMigrate -Cfg $Cfg -Action $Action -Target $Target -NoBuild:$NoBuild
        } else {
            Invoke-HartMigrate -Cfg $Cfg -Action $Action -NoBuild:$NoBuild
        }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "migrate $Action complete."
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
