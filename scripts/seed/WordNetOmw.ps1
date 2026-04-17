#requires -Version 7
<#
.SYNOPSIS
  Run the combined WordNetOmw phase (Princeton WN 3.0 + Open Multilingual WN).

.EXAMPLE
  pwsh scripts/seed/WordNetOmw.ps1
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
Start-HartonomousLog -ScriptName 'seed.WordNetOmw' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.WordNet) -Label 'Princeton WordNet dict/'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Omw)     -Label 'OMW root'
    Invoke-HartStep -Name 'Phase: WordNetOmw' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'WordNetOmw' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'WordNet + OMW seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
