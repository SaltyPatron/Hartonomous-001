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

    # Semantic seed floor first for a full enriched seed run: codepoints +
    # collation, languages, lemmas/senses, syntactic structure, lexicon depth,
    # attested sentences. ModelDecomp only hard-requires the foundation
    # substrate (UCD/UCA + ISO), but seed.All runs it after semantic seeds when
    # -WithModel is requested so model-derived entities immediately converge
    # with the richest available grounding evidence.
    Invoke-HartStep -Name 'seed.Ucd'           -Action { Invoke-Sub 'Ucd.ps1'           $commonArgs }
    Invoke-HartStep -Name 'seed.Iso639'        -Action { Invoke-Sub 'Iso639.ps1'        $commonArgs }
    Invoke-HartStep -Name 'seed.WordNetOmw'    -Action { Invoke-Sub 'WordNetOmw.ps1'    $commonArgs }
    Invoke-HartStep -Name 'seed.UniversalDeps' -Action { Invoke-Sub 'UniversalDeps.ps1' $commonArgs }
    Invoke-HartStep -Name 'seed.Wiktionary'    -Action { Invoke-Sub 'Wiktionary.ps1'    $commonArgs }
    Invoke-HartStep -Name 'seed.Tatoeba'       -Action { Invoke-Sub 'Tatoeba.ps1'       $commonArgs }

    if ($WithModel) {
        # Full seed mode runs ModelDecomp after semantic enrichment. The phase
        # DAG itself still allows targeted Safetensors ingestion after the
        # foundation phases only.
        Invoke-HartStep -Name 'seed.Safetensors' -Action { Invoke-Sub 'Safetensors.ps1' $commonArgs }
    }

    # Post-W2E: edge significance is primed at end of each phase by the
    # StreamingIngestionPipeline.PrimeAllSignificanceAsync call inside
    # FlushAsync (which iterates the arena list at call time and loops
    # substrate.prime_unprimed_edges_chunk per arena). No separate
    # "SignificanceField phase" needed; no continuous background loop;
    # no SignificanceField step here. Glicko-2 ratings update at inference
    # time via substrate.record_comparison / record_corroboration on real
    # outcomes.

    Invoke-HartStep -Name 'Validate' -Action {
        Invoke-Sub 'Validate.ps1' @()
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Seed.All complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
