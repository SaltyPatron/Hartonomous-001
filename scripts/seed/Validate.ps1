#requires -Version 7
<#
.SYNOPSIS
  Print post-seed substrate row counts.

.EXAMPLE
  pwsh scripts/seed/Validate.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'seed.Validate' -Cfg $Cfg

try {
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }
    Write-HartBanner 'Substrate row counts'
    $counts = Get-HartSubstrateCounts -Cfg $Cfg
    foreach ($kv in $counts.GetEnumerator()) {
        '{0,-32} {1,10}' -f $kv.Key, $kv.Value | Write-Host
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
