#requires -Version 7
<#
.SYNOPSIS
  Wraps `Hartonomous.Cli session ...` (open, close, status).

.PARAMETER Action
  open | close | status.

.PARAMETER SessionId
  Required for -Action close.

.EXAMPLE
  pwsh scripts/ops/Session.ps1 -Action open
  pwsh scripts/ops/Session.ps1 -Action close -SessionId 42
#>
[CmdletBinding()]
param(
    [ValidateSet('open','close','status')] [string]$Action = 'status',
    [int]$SessionId,
    [switch]$NoBuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName "ops.Session.$Action" -Cfg $Cfg

try {
    $cliArgs = @('session', $Action, '--connection', $Cfg.Postgres.ConnectionString)
    if ($Action -eq 'close') {
        if (-not $PSBoundParameters.ContainsKey('SessionId')) {
            Write-HartError '-SessionId is required for close.'
            Exit-Hartonomous -Code $Cfg.ExitCodes.Usage
        }
        $cliArgs += @('--id', $SessionId)
    }
    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs $cliArgs
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
