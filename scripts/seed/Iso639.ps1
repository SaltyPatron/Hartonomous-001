#requires -Version 7
<#
.SYNOPSIS
  Run the Iso639 seed phase.

.EXAMPLE
  pwsh scripts/seed/Iso639.ps1
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [switch]$SkipDeps,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
Start-HartonomousLog -ScriptName 'seed.Iso639' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Iso639) -Label 'ISO 639-3 tab file'
    Invoke-HartStep -Name 'Phase: Iso639' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'Iso639' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Iso639 seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
