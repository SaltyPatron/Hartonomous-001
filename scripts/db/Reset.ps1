#requires -Version 7
<#
.SYNOPSIS
  Drop + recreate the substrate database. Destructive.

.DESCRIPTION
  Convenience wrapper around Drop + Create + Bootstrap. Use this when you
  want the DB back to a pristine bootstrap-applied (but unseeded) state.
  Pre-v1 the substrate is bootstrap-only; sql/schema/ is the canonical
  source of truth.

.PARAMETER Force
  Skip confirmation prompts on the Drop step.

.PARAMETER NoBootstrap
  Skip running bootstrap after recreating (leaves an empty database).

.EXAMPLE
  pwsh scripts/db/Reset.ps1 -Force
  pwsh scripts/db/Reset.ps1 -Force -NoBootstrap
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [switch]$Force,
    [switch]$NoBootstrap
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Reset' -Cfg $Cfg

function Invoke-Sub { param([string]$Script, [string[]]$Argv)
    pwsh -File (Join-Path $PSScriptRoot $Script) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Script failed (exit $LASTEXITCODE)." }
}

try {
    Invoke-HartStep -Name 'Drop database' -Action {
        $dropArgs = @()
        if ($Force) { $dropArgs += '-Force' }
        Invoke-Sub 'Drop.ps1' $dropArgs
    }
    Invoke-HartStep -Name 'Create database' -Action {
        Invoke-Sub 'Create.ps1' @()
    }
    if (-not $NoBootstrap) {
        Invoke-HartStep -Name 'bootstrap' -Action {
            Invoke-Sub 'Bootstrap.ps1' @()
        }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Database reset complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
