#requires -Version 7
<#
.SYNOPSIS
  Run every test suite: native (ctest) → dotnet unit → pg_regress → integration.

.PARAMETER SkipNative | SkipDotnet | SkipPg | SkipIntegration
  Selective skips.

.EXAMPLE
  pwsh scripts/test/All.ps1
  pwsh scripts/test/All.ps1 -SkipIntegration
#>
[CmdletBinding()]
param(
    [switch]$SkipNative,
    [switch]$SkipDotnet,
    [switch]$SkipPg,
    [switch]$SkipIntegration,
    [ValidateSet('Debug','Release')] [string]$DotnetConfiguration,
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$NativeConfiguration
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
$dotnetCfg = if ($DotnetConfiguration) { $DotnetConfiguration } else { $Cfg.Dotnet.Configuration }
$nativeCfg = if ($NativeConfiguration) { $NativeConfiguration } else { $Cfg.Native.Configuration }

Start-HartonomousLog -ScriptName 'test.All' -Cfg $Cfg

function Invoke-Sub { param([string]$Script, [string[]]$Argv)
    pwsh -File (Join-Path $PSScriptRoot $Script) @Argv
    if ($LASTEXITCODE -ne 0) { throw "$Script failed (exit $LASTEXITCODE)." }
}

try {
    if (-not $SkipNative)      { Invoke-HartStep -Name 'Native tests'      -Action { Invoke-Sub 'Native.ps1'      @('-Configuration', $nativeCfg) } }
    if (-not $SkipDotnet)      { Invoke-HartStep -Name 'Dotnet tests'      -Action { Invoke-Sub 'Dotnet.ps1'      @('-Configuration', $dotnetCfg) } }

    $pgRunning = Test-HartContainerRunning -Name $Cfg.Docker.PgContainer
    if (-not $SkipPg) {
        if ($pgRunning) { Invoke-HartStep -Name 'pg_regress' -Action { Invoke-Sub 'Pg.ps1' @() } }
        else { Write-HartWarn 'Skipping pg_regress — container not running.' }
    }
    if (-not $SkipIntegration) {
        if ($pgRunning) { Invoke-HartStep -Name 'Integration tests' -Action { Invoke-Sub 'Integration.ps1' @('-Configuration', $dotnetCfg) } }
        else { Write-HartWarn 'Skipping integration tests — container not running.' }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'All requested test suites passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
