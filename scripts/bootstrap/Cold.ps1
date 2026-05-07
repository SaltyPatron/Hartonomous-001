#requires -Version 7
<#
.SYNOPSIS
  Cold machine → running substrate seeded with UCD + ISO639 + WordNet + OMW.

.DESCRIPTION
  Composes every piece of the matrix. Intended for first-time setup, after
  `git clone`, or after a disk wipe. All destructive steps are guarded — pass
  -Recreate to force a DB reset, -Rebuild to force image/native rebuild.

.PARAMETER Recreate
  Drop & recreate the database before bootstrapping (destroys substrate data).

.PARAMETER Rebuild
  Rebuild the Postgres image and the native library from scratch.

.PARAMETER WithModel
  Also run the Safetensors (ModelDecomp) phase.

.PARAMETER SkipTests
  Skip test suites during the bootstrap (faster — you can always run them
  after with scripts/test/All.ps1).

.EXAMPLE
  pwsh scripts/bootstrap/Cold.ps1
  pwsh scripts/bootstrap/Cold.ps1 -Recreate -Rebuild -WithModel
#>
[CmdletBinding()]
param(
    [switch]$Recreate,
    [switch]$Rebuild,
    [switch]$WithModel,
    [switch]$SkipTests,
    [ValidateSet('Debug','Release')] [string]$DotnetConfiguration,
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$NativeConfiguration
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
$dotnetCfg = if ($DotnetConfiguration) { $DotnetConfiguration } else { $Cfg.Dotnet.Configuration }
$nativeCfg = if ($NativeConfiguration) { $NativeConfiguration } else { $Cfg.Native.Configuration }

Start-HartonomousLog -ScriptName 'bootstrap.Cold' -Cfg $Cfg

function Invoke-Sub { param([string]$Rel, [string[]]$Argv)
    pwsh -File (Join-Path (Split-Path $PSScriptRoot -Parent) $Rel) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Rel failed (exit $LASTEXITCODE)." }
}

try {
    Invoke-HartStep -Name 'Preflight' -Action { Invoke-Sub 'ci/Preflight.ps1' @() }

    Invoke-HartStep -Name 'Extension SQL' -Action { Invoke-Sub 'build/ExtensionSql.ps1' @() }

    Invoke-HartStep -Name 'Docker Desktop' -Action { Invoke-Sub 'docker/Start-Desktop.ps1' @() }

    $upArgs = @()
    if ($Rebuild) { $upArgs += '-Rebuild' }
    Invoke-HartStep -Name 'Postgres up' -Action { Invoke-Sub 'docker/Up.ps1' $upArgs }

    $nativeArgs = @('-Configuration', $nativeCfg)
    if ($Rebuild) { $nativeArgs += '-Clean' }
    Invoke-HartStep -Name "Build native ($nativeCfg)"  -Action { Invoke-Sub 'build/Native.ps1' $nativeArgs }
    Invoke-HartStep -Name "Build managed ($dotnetCfg)" -Action { Invoke-Sub 'build/Dotnet.ps1' @('-Configuration', $dotnetCfg) }

    if (-not $SkipTests) {
        Invoke-HartStep -Name 'Native tests' -Action { Invoke-Sub 'test/Native.ps1' @('-Configuration', $nativeCfg) }
        Invoke-HartStep -Name 'Dotnet tests' -Action { Invoke-Sub 'test/Dotnet.ps1' @('-Configuration', $dotnetCfg, '-NoBuild') }
    }

    if ($Recreate) {
        Invoke-HartStep -Name 'DB reset' -Action { Invoke-Sub 'db/Reset.ps1' @('-Force') }
    } else {
        Invoke-HartStep -Name 'DB create (idempotent)' -Action { Invoke-Sub 'db/Create.ps1' @() }
        Invoke-HartStep -Name 'Bootstrap extension'    -Action { Invoke-Sub 'db/Bootstrap.ps1' @() }
    }

    Invoke-HartStep -Name 'Seed UcdUca'         -Action { Invoke-Sub 'seed/Ucd.ps1'        @('-NoBuild') }
    Invoke-HartStep -Name 'Seed Iso639'         -Action { Invoke-Sub 'seed/Iso639.ps1'     @('-NoBuild') }
    Invoke-HartStep -Name 'Seed WordNetOmw'     -Action { Invoke-Sub 'seed/WordNetOmw.ps1' @('-NoBuild') }
    if ($WithModel) {
        Invoke-HartStep -Name 'Seed Safetensors' -Action { Invoke-Sub 'seed/Safetensors.ps1' @('-NoBuild') }
    }

    Invoke-HartStep -Name 'Final status'    -Action { Invoke-Sub 'ops/Status.ps1'   @() }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Cold bootstrap complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
