#requires -Version 7
<#
.SYNOPSIS
  Bring up the hartonomous-postgres container (compose up -d) and wait healthy.

.PARAMETER Rebuild
  Force a rebuild of the image (compose up -d --build).

.PARAMETER SkipDesktopStart
  Don't attempt to start Docker Desktop; assume the daemon is already up.

.EXAMPLE
  pwsh scripts/docker/Up.ps1
  pwsh scripts/docker/Up.ps1 -Rebuild
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [switch]$SkipDesktopStart
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Up' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    if (-not $SkipDesktopStart) {
        Invoke-HartStep -Name 'Ensure Docker daemon' -Action { Start-HartDockerDesktop -Cfg $Cfg }
    }
    Invoke-HartStep -Name "compose up [$($Cfg.Docker.PgContainer)]" -Action {
        if ($Rebuild) {
            Invoke-HartCompose -Cfg $Cfg -Argv @('up', '-d', '--build')
        } else {
            Invoke-HartCompose -Cfg $Cfg -Argv @('up', '-d')
        }
    }
    Invoke-HartStep -Name "Wait for $($Cfg.Docker.PgContainer) to be healthy" -Action {
        Wait-HartContainerHealthy -Name $Cfg.Docker.PgContainer -TimeoutSec $Cfg.Docker.HealthCheckTimeoutSec
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "$($Cfg.Docker.PgContainer) is healthy."
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
}
