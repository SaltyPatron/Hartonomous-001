#requires -Version 7
<#
.SYNOPSIS
  Run the SafetensorsDecomposer (ModelDecomp phase).

.PARAMETER SourceRoot
  Root that contains the HuggingFace-style model directory. Default uses the
  configured D:\Models (the CLI resolves a specific model subdir itself).

.EXAMPLE
  pwsh scripts/seed/Safetensors.ps1
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
Start-HartonomousLog -ScriptName 'seed.Safetensors' -Cfg $Cfg

try {
    Invoke-HartStep -Name 'Phase: ModelDecomp (Safetensors)' -Action {
        Invoke-HartPhase -Cfg $Cfg -Phase 'ModelDecomp' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Safetensors ingested.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
