#requires -Version 7
<#
.SYNOPSIS
  Wraps `Hartonomous.Cli session ...`.

.PARAMETER Action
  create | close | list | archive | show.

.PARAMETER SessionId
  Required for -Action archive or show.

.EXAMPLE
  pwsh scripts/ops/Session.ps1 -Action create
  pwsh scripts/ops/Session.ps1 -Action show -SessionId 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding()]
param(
    [ValidateSet('create','close','list','archive','show')] [string]$Action = 'list',
    [guid]$SessionId,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName "ops.Session.$Action" -Cfg $Cfg

try {
  $cliArgs = @('session', $Action)
  if ($Action -in @('archive', 'show')) {
        if (-not $PSBoundParameters.ContainsKey('SessionId')) {
      Write-HartError "-SessionId is required for $Action."
            Exit-Hartonomous -Code $Cfg.ExitCodes.Usage
        }
    $cliArgs += @($SessionId.ToString())
    }

      $previousConnectionString = $env:HARTONOMOUS__Hartonomous__ConnectionString
      try {
        $env:HARTONOMOUS__Hartonomous__ConnectionString = $Cfg.Postgres.ConnectionString
        Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs $cliArgs
      }
      finally {
        if ($null -eq $previousConnectionString) {
          Remove-Item Env:HARTONOMOUS__Hartonomous__ConnectionString -ErrorAction SilentlyContinue
        }
        else {
          $env:HARTONOMOUS__Hartonomous__ConnectionString = $previousConnectionString
        }
      }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
