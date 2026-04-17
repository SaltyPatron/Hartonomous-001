#requires -Version 7
<#
.SYNOPSIS
  Ensure Docker Desktop is running and the daemon is reachable.

.DESCRIPTION
  Idempotent. If the daemon already responds, returns immediately. Otherwise
  launches Docker Desktop (searching the known install locations) and waits up
  to Docker.DesktopStartTimeoutSec seconds for the daemon to come online.
  Windows-only (Linux/macOS should start dockerd/Docker Desktop via the host
  service manager).

.EXAMPLE
  pwsh scripts/docker/Start-Desktop.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Start-Desktop' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' -InstallHint 'install Docker Desktop from https://www.docker.com/products/docker-desktop/' | Out-Null
    Invoke-HartStep -Name 'Ensure Docker daemon is running' -Action {
        Start-HartDockerDesktop -Cfg $Cfg
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Docker daemon is up.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable -Message 'Failed to reach Docker daemon.'
}
