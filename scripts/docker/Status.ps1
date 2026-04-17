#requires -Version 7
<#
.SYNOPSIS
  Report container + health + port exposure for the compose stack.

.EXAMPLE
  pwsh scripts/docker/Status.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Status' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null

    $daemon = if (Test-HartDockerDaemon) { 'up' } else { 'down' }
    Write-Host ("Docker daemon: {0}" -f $daemon)

    $exists = Test-HartContainerExists -Name $Cfg.Docker.PgContainer
    $running = Test-HartContainerRunning -Name $Cfg.Docker.PgContainer
    $health = Get-HartContainerHealth -Name $Cfg.Docker.PgContainer

    Write-Host ("Container:     {0}" -f $Cfg.Docker.PgContainer)
    Write-Host ("  exists:      {0}" -f $exists)
    Write-Host ("  running:     {0}" -f $running)
    Write-Host ("  health:      {0}" -f $health)

    if ($running) {
        $ports = & docker port $Cfg.Docker.PgContainer 2>$null
        Write-Host '  ports:'
        $ports | ForEach-Object { Write-Host "    $_" }
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
