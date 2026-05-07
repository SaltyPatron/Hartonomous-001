#requires -Version 7
<#
.SYNOPSIS
  Dev inner-loop: up the container, bootstrap the extension, build everything.
  Does NOT reseed and does NOT run tests — use this when you've made code
  changes and just want a fresh binary/schema state to iterate against.

.PARAMETER Rebuild
  Force container image rebuild + native clean rebuild.

.EXAMPLE
  pwsh scripts/bootstrap/Dev.ps1
  pwsh scripts/bootstrap/Dev.ps1 -Rebuild
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [ValidateSet('Debug','Release')] [string]$DotnetConfiguration,
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$NativeConfiguration
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
$dotnetCfg = if ($DotnetConfiguration) { $DotnetConfiguration } else { $Cfg.Dotnet.Configuration }
$nativeCfg = if ($NativeConfiguration) { $NativeConfiguration } else { $Cfg.Native.Configuration }

Start-HartonomousLog -ScriptName 'bootstrap.Dev' -Cfg $Cfg

function Invoke-Sub { param([string]$Rel, [string[]]$Argv)
    pwsh -File (Join-Path (Split-Path $PSScriptRoot -Parent) $Rel) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Rel failed (exit $LASTEXITCODE)." }
}

try {
    Invoke-HartStep -Name 'Extension SQL'     -Action { Invoke-Sub 'build/ExtensionSql.ps1' @() }
    Invoke-HartStep -Name 'Docker Desktop'    -Action { Invoke-Sub 'docker/Start-Desktop.ps1' @() }
    $upArgs = @()
    if ($Rebuild) { $upArgs += '-Rebuild' }
    Invoke-HartStep -Name 'Postgres up'       -Action { Invoke-Sub 'docker/Up.ps1' $upArgs }

    $nativeArgs = @('-Configuration', $nativeCfg)
    if ($Rebuild) { $nativeArgs += '-Clean' }
    Invoke-HartStep -Name "Build native ($nativeCfg)"  -Action { Invoke-Sub 'build/Native.ps1' $nativeArgs }
    Invoke-HartStep -Name "Build managed ($dotnetCfg)" -Action { Invoke-Sub 'build/Dotnet.ps1' @('-Configuration', $dotnetCfg) }

    Invoke-HartStep -Name 'Ensure DB'           -Action { Invoke-Sub 'db/Create.ps1' @() }
    Invoke-HartStep -Name 'Bootstrap extension' -Action { Invoke-Sub 'db/Bootstrap.ps1' @() }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Dev loop ready.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
