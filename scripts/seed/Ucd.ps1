#requires -Version 7
<#
.SYNOPSIS
  Operator wrapper for the UcdUca phase.

.DESCRIPTION
  UCD/UCA seed semantics are owned by the C# phase runner and
  Hartonomous.Decomposers.Ucd.UcdUcaDecomposer. This script is only a
  convenience entrypoint; it must not duplicate SQL materialization logic or
  mark monitor.phase_status directly.

.PARAMETER SourceRoot
  Root containing the configured Unicode source drop.

.PARAMETER SkipDeps
  Pass --skip-deps to the phase runner.

.PARAMETER Force
  Pass --force to rerun UcdUca even if monitor.phase_status says completed.

.PARAMETER NoBuild
  Pass --no-build to dotnet run.

.PARAMETER Connection
  PostgreSQL connection string for the phase runner.

.EXAMPLE
  pwsh scripts/seed/Ucd.ps1
  pwsh scripts/seed/Ucd.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [switch]$SkipDeps,
    [switch]$Force,
    [switch]$NoBuild,
    [string]$Connection = "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous"
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
Start-HartonomousLog -ScriptName 'seed.Ucd' -Cfg $Cfg

try {
    $cliArgs = @(
        'phases', 'run',
        '--phase', 'UcdUca',
        '--connection', $Connection,
        '--source', $SourceRoot
    )
    if ($SkipDeps) { $cliArgs += '--skip-deps' }
    if ($Force) { $cliArgs += '--force' }

    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs $cliArgs
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'UcdUca phase completed through the canonical phase runner.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
