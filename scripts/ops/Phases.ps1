#requires -Version 7
<#
.SYNOPSIS
  Thin wrapper around `Hartonomous.Cli phases {list|status|run}`.

.PARAMETER Action
  list (default) | status | run.

.PARAMETER Phase
  Required when -Action run.

.EXAMPLE
  pwsh scripts/ops/Phases.ps1 -Action list
  pwsh scripts/ops/Phases.ps1 -Action status
  pwsh scripts/ops/Phases.ps1 -Action run -Phase UcdUca
#>
[CmdletBinding()]
param(
    [ValidateSet('list','status','run')] [string]$Action = 'list',
    [string]$Phase,
    [string]$SourceRoot,
    [switch]$SkipDeps,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName "ops.Phases.$Action" -Cfg $Cfg

try {
    switch ($Action) {
        'list'   { Get-HartPhaseList   -Cfg $Cfg -NoBuild:$NoBuild }
        'status' { Get-HartPhaseStatus -Cfg $Cfg -NoBuild:$NoBuild }
        'run' {
            if (-not $Phase) {
                Write-HartError '-Phase is required for -Action run.'
                Exit-Hartonomous -Code $Cfg.ExitCodes.Usage
            }
            if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
            Invoke-HartPhase -Cfg $Cfg -Phase $Phase -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
        }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
