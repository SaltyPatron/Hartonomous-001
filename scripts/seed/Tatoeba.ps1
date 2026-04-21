#requires -Version 7
<#
.SYNOPSIS
  Run the Tatoeba seed phase. Three-pass decomposer:
    1. sentences.csv              → tatoeba_sentence + text_composition + has_text + entity_language
    2. links.csv                  → translation_link edges between sentences
    3. audio/sentences_with_audio.csv → audio_recording + recording_of + has_contributor

.EXAMPLE
  pwsh scripts/seed/Tatoeba.ps1
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
Start-HartonomousLog -ScriptName 'seed.Tatoeba' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.TatoebaRoot)          -Label 'Tatoeba root'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.TatoebaSentences)     -Label 'Tatoeba sentences.csv'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.TatoebaLinks)         -Label 'Tatoeba links.csv'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.TatoebaAudioManifest) -Label 'Tatoeba audio manifest (sentences_with_audio.csv)'

    Invoke-HartStep -Name 'Phase: Tatoeba' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'Tatoeba' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Tatoeba seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
