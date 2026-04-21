#requires -Version 7
<#
.SYNOPSIS
  Run the Wiktionary seed phase. Streams raw-wiktextract-data.jsonl
  into the substrate — lemmas, inflections, senses, translations, relations,
  etymology templates, sound entries, hyphenations, examples, and every
  character-sequence composition underneath them.

.EXAMPLE
  pwsh scripts/seed/Wiktionary.ps1
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
Start-HartonomousLog -ScriptName 'seed.Wiktionary' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.WiktionaryRoot)  -Label 'Wiktionary root'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.WiktionaryJsonl) -Label 'wiktextract JSONL'
    Invoke-HartStep -Name 'Phase: Wiktionary' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'Wiktionary' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Wiktionary seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
