#requires -Version 7
<#
.SYNOPSIS
  Run every seed phase in FK dependency order, then prime edge-level
  significance so inference can differentiate paths.

.DESCRIPTION
  Order mirrors the PhaseDag:
    UcdUca → Iso639 → WordNetOmw → UniversalDeps → Wiktionary → Tatoeba
    (optional) ModelDecomp
    SignificanceField — primes edge significance from provenance trust priors

  All seed sources contribute substantial English-side semantic content for
  T0 (English-only ingestion):
    * Wiktionary — definitions, etymologies, IPA, inflections, examples,
      synonyms/antonyms/hypernyms/hyponyms/meronyms, Wikidata cross-refs,
      hyphenation. The LanguageFilter (default eng-only) bounds the input
      JSONL to English entries.
    * Tatoeba — ~1.5M attested English sentences as usage corpus + audio
      recordings of English speakers.
  These are not "translation dictionaries" — they are core T0 substrate.

  ModelDecomp is opt-in via -WithModel since it ingests safetensors models
  which are a separate modality from the lexical seed chain.

.PARAMETER WithModel
  Also run ModelDecomp (Safetensors ingestion).

.EXAMPLE
  pwsh scripts/seed/All.ps1
  pwsh scripts/seed/All.ps1 -WithModel
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [switch]$WithModel,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
Start-HartonomousLog -ScriptName 'seed.All' -Cfg $Cfg

function Invoke-Sub { param([string]$Script, [string[]]$Argv)
    pwsh -File (Join-Path $PSScriptRoot $Script) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Script failed (exit $LASTEXITCODE)." }
}

try {
    $commonArgs = @('-SourceRoot', $SourceRoot)
    if ($NoBuild) { $commonArgs += '-NoBuild' }

    Invoke-HartStep -Name 'seed.Ucd'           -Action { Invoke-Sub 'Ucd.ps1'           $commonArgs }
    Invoke-HartStep -Name 'seed.Iso639'        -Action { Invoke-Sub 'Iso639.ps1'        $commonArgs }
    Invoke-HartStep -Name 'seed.WordNetOmw'    -Action { Invoke-Sub 'WordNetOmw.ps1'    $commonArgs }
    Invoke-HartStep -Name 'seed.UniversalDeps' -Action { Invoke-Sub 'UniversalDeps.ps1' $commonArgs }
    Invoke-HartStep -Name 'seed.Wiktionary'    -Action { Invoke-Sub 'Wiktionary.ps1'    $commonArgs }
    Invoke-HartStep -Name 'seed.Tatoeba'       -Action { Invoke-Sub 'Tatoeba.ps1'       $commonArgs }

    if ($WithModel) {
        Invoke-HartStep -Name 'seed.Safetensors' -Action { Invoke-Sub 'Safetensors.ps1' $commonArgs }
    }

    # Master plan #61 — prime edge-level significance from provenance trust
    # priors. Without this, every edge in every arena sits at the Glicko-2
    # default of 1500 and inference can't rank paths. Must run after all
    # phases that emit edges.
    Invoke-HartStep -Name 'seed.SignificanceField' -Action { Invoke-Sub 'SignificanceField.ps1' $commonArgs }

    Invoke-HartStep -Name 'Validate' -Action {
        Invoke-Sub 'Validate.ps1' @()
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Seed.All complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
