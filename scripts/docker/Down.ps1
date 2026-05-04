#requires -Version 7
<#
.SYNOPSIS
  Stop the compose stack. Preserves volumes by default.

.PARAMETER RemoveVolumes
  Also remove the pgdata volume (destructive — you lose the database).

.PARAMETER Force
  Skip the interactive confirmation prompt when -RemoveVolumes is set.
  Mirrors scripts/db/Drop.ps1 -Force; required for unattended pipelines
  like RunAll.bat that would otherwise hang on the [Y/n] prompt.

.EXAMPLE
  pwsh scripts/docker/Down.ps1
  pwsh scripts/docker/Down.ps1 -RemoveVolumes -Force
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [switch]$RemoveVolumes,
    [switch]$Force
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Down' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    $argv = @('down')
    if ($RemoveVolumes) {
        if (-not $Force -and -not $PSCmdlet.ShouldProcess($Cfg.Docker.ComposeProject, 'compose down -v (destroys pgdata)')) {
            Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Aborted.'
        }
        $argv += '-v'
    }
    Invoke-HartStep -Name "compose $($argv -join ' ')" -Action {
        Invoke-HartCompose -Cfg $Cfg -Argv $argv
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Stack is down.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
