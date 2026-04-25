#requires -Version 7
<#
.SYNOPSIS
  Run the SignificanceField phase — primes edge-level significance from each
  edge's provenance trust prior. Without this, every edge sits at the Glicko-2
  default of 1500 in every arena, and inference can't differentiate paths.

  Master plan #61 — replaces the prior SignificanceField stub.

.DESCRIPTION
  Should be run AFTER all seed phases that populate edges (UCD → ISO 639 →
  WordNetOmw → UniversalDeps → optional Wiktionary / Tatoeba). Idempotent:
  re-running adds no new rows because the INSERT uses ON CONFLICT DO NOTHING.

.EXAMPLE
  pwsh scripts/seed/SignificanceField.ps1
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
Start-HartonomousLog -ScriptName 'seed.SignificanceField' -Cfg $Cfg

try {
    Invoke-HartStep -Name 'Phase: SignificanceField' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'SignificanceField' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'SignificanceField primed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
