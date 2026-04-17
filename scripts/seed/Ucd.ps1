#requires -Version 7
<#
.SYNOPSIS
  Run the UcdUca seed phase (Unicode codepoint properties + UCA collation).

.PARAMETER SourceRoot
  Root containing UCD/Public/UCD/latest. Default from config.psd1.

.PARAMETER SkipDeps
  Don't re-run upstream phases even if their state is missing.

.EXAMPLE
  pwsh scripts/seed/Ucd.ps1
  pwsh scripts/seed/Ucd.ps1 -SourceRoot D:\Models
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
Start-HartonomousLog -ScriptName 'seed.Ucd' -Cfg $Cfg

try {
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Ucd) -Label 'UCD XML'
    Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Uca) -Label 'UCA allkeys.txt'

    Invoke-HartStep -Name 'Phase: UcdUca' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'UcdUca' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'UcdUca seeded.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
