#requires -Version 7
<#
.SYNOPSIS
  Run the UniversalDeps seed phase. Walks every UD_{Language}-{Treebank}
  directory under ud-treebanks-v2.17 and streams all train/dev/test
  *.conllu files into the substrate — sentences, tokens, lemmas,
  POS, morph features, and labelled dependency edges.

.EXAMPLE
  pwsh scripts/seed/UniversalDeps.ps1
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
Start-HartonomousLog -ScriptName 'seed.UniversalDeps' -Cfg $Cfg

try {
    $udRoot = Join-Path $SourceRoot $Cfg.Seed.UniversalDepsRoot
    Assert-HartPath -Path $udRoot -Label 'UD treebanks root'

    # Sanity: at least one UD_* treebank dir must be present with .conllu files.
    $treebanks = Get-ChildItem -Path $udRoot -Directory -Filter 'UD_*' -ErrorAction Stop
    if (-not $treebanks -or $treebanks.Count -eq 0) {
        throw "No UD_* treebank directories found under $udRoot"
    }
    $withConllu = @($treebanks | Where-Object {
        (Get-ChildItem -Path $_.FullName -Filter '*.conllu' -ErrorAction SilentlyContinue).Count -gt 0
    })
    if ($withConllu.Count -eq 0) {
        throw "Found $($treebanks.Count) UD_* directories under $udRoot but none contain .conllu files"
    }
    Write-HartInfo "UD: $($withConllu.Count)/$($treebanks.Count) treebanks have .conllu files under $udRoot"

    Invoke-HartStep -Name 'Phase: UniversalDeps' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'UniversalDeps' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Universal Dependencies seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
