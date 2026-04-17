#requires -Version 7
<#
.SYNOPSIS
  Run every seed phase in FK dependency order.

.DESCRIPTION
  Order mirrors the PhaseDag: UcdUca → Iso639 → WordNetOmw → (optional) ModelDecomp.
  ModelDecomp is heavy and is skipped by default — opt in with -WithModel.

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

    Invoke-HartStep -Name 'seed.Ucd'        -Action { Invoke-Sub 'Ucd.ps1'        $commonArgs }
    Invoke-HartStep -Name 'seed.Iso639'     -Action { Invoke-Sub 'Iso639.ps1'     $commonArgs }
    Invoke-HartStep -Name 'seed.WordNetOmw' -Action { Invoke-Sub 'WordNetOmw.ps1' $commonArgs }
    if ($WithModel) {
        Invoke-HartStep -Name 'seed.Safetensors' -Action { Invoke-Sub 'Safetensors.ps1' $commonArgs }
    }

    Invoke-HartStep -Name 'Validate' -Action {
        Invoke-Sub 'Validate.ps1' @()
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Seed.All complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
