#requires -Version 7
<#
.SYNOPSIS
  The canonical CI pipeline: preflight → build → test → up → migrate → smoke.

.DESCRIPTION
  Same ordering as .github/workflows/ci.yml so a local run catches the same
  things CI does. Keeps going through all build+test steps — does not skip to
  smoke if something fails earlier.

.PARAMETER DotnetConfiguration
  Debug | Release.

.PARAMETER NativeConfiguration
  Debug | Release | RelWithDebInfo | MinSizeRel.

.PARAMETER SkipSeed
  Skip the smoke seed (UcdUca + Iso639) — useful for fast local loops.

.EXAMPLE
  pwsh scripts/ci/Pipeline.ps1
  pwsh scripts/ci/Pipeline.ps1 -DotnetConfiguration Release -NativeConfiguration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$DotnetConfiguration = 'Release',
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$NativeConfiguration = 'Release',
    [switch]$SkipSeed
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'ci.Pipeline' -Cfg $Cfg

function Invoke-Sub { param([string]$Rel, [string[]]$Argv)
    pwsh -File (Join-Path (Split-Path $PSScriptRoot -Parent) $Rel) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Rel failed (exit $LASTEXITCODE)." }
}

try {
    Invoke-HartStep -Name 'Preflight'         -Action { Invoke-Sub 'ci/Preflight.ps1'   @() }
    Invoke-HartStep -Name 'Build native'      -Action { Invoke-Sub 'build/Native.ps1'   @('-Configuration', $NativeConfiguration) }
    Invoke-HartStep -Name 'Build managed'     -Action { Invoke-Sub 'build/Dotnet.ps1'   @('-Configuration', $DotnetConfiguration) }
    Invoke-HartStep -Name 'Test native'       -Action { Invoke-Sub 'test/Native.ps1'    @('-Configuration', $NativeConfiguration) }
    Invoke-HartStep -Name 'Test managed'      -Action { Invoke-Sub 'test/Dotnet.ps1'    @('-Configuration', $DotnetConfiguration, '-NoBuild') }
    Invoke-HartStep -Name 'Docker up'         -Action { Invoke-Sub 'docker/Up.ps1'      @() }
    Invoke-HartStep -Name 'Migrate up'        -Action { Invoke-Sub 'db/Migrate.ps1'     @('-Action', 'up', '-NoBuild') }
    if (-not $SkipSeed) {
        Invoke-HartStep -Name 'Seed UcdUca'   -Action { Invoke-Sub 'seed/Ucd.ps1'       @('-NoBuild') }
        Invoke-HartStep -Name 'Seed Iso639'   -Action { Invoke-Sub 'seed/Iso639.ps1'    @('-NoBuild') }
        Invoke-HartStep -Name 'Validate'      -Action { Invoke-Sub 'seed/Validate.ps1'  @() }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Pipeline green.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
