#requires -Version 7
<#
.SYNOPSIS
  Destroy the compose stack AND remove the pgdata volume + image.

.DESCRIPTION
  Irreversible. All substrate data is lost. Requires -Confirm or interactive
  confirmation via SupportsShouldProcess.

.PARAMETER RemoveImage
  Also remove the built hartonomous-postgres image so the next `Up -Rebuild`
  starts from scratch.

.EXAMPLE
  pwsh scripts/docker/Teardown.ps1 -Confirm
  pwsh scripts/docker/Teardown.ps1 -RemoveImage -Force
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [switch]$RemoveImage,
    [switch]$Force
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Teardown' -Cfg $Cfg

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    $target = "$($Cfg.Docker.ComposeProject) (including pgdata volume)"
    if (-not $Force -and -not $PSCmdlet.ShouldProcess($target, 'Destroy stack and volumes')) {
        Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Aborted.'
    }

    Invoke-HartStep -Name 'compose down -v --remove-orphans' -Action {
        Invoke-HartCompose -Cfg $Cfg -Argv @('down', '-v', '--remove-orphans')
    }

    if ($RemoveImage) {
        Invoke-HartStep -Name "docker image rm $($Cfg.Docker.PgImage)" -Action {
            # Non-fatal — image may already be gone.
            & docker image rm $Cfg.Docker.PgImage 2>$null
        } -ContinueOnError
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Teardown complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
