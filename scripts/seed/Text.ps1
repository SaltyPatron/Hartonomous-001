#requires -Version 7
<#
.SYNOPSIS
  Run the TextDecomp phase (UAX #29 text decomposition).

.DESCRIPTION
  Decomposes every .txt file under test_data/text into codepoints,
  grapheme clusters, words, sentences, and document entities using
  the UAX #29 segmentation stack.

.PARAMETER SourceRoot
  Root containing test_data/text. Default from config.psd1.

.PARAMETER SkipDeps
  Don't re-run upstream phases even if their state is missing.

.EXAMPLE
  pwsh scripts/seed/Text.ps1
  pwsh scripts/seed/Text.ps1 -SourceRoot /vault/Data
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
Start-HartonomousLog -ScriptName 'seed.Text' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.TextRoot) -Label 'Text directory'

    Invoke-HartStep -Name 'Phase: TextDecomp' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'TextDecomp' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'TextDecomp seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
