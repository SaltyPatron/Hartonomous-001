#requires -Version 7
<#
.SYNOPSIS
  Stream (or tail) logs from the Postgres container.

.PARAMETER Follow
  Stream new log lines.

.PARAMETER Tail
  Number of historical lines to show. Default 200.

.EXAMPLE
  pwsh scripts/docker/Logs.ps1 -Follow
  pwsh scripts/docker/Logs.ps1 -Tail 50
#>
[CmdletBinding()]
param(
    [switch]$Follow,
    [int]$Tail = 200
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Logs' -Cfg $Cfg

$argv = @('logs', '--tail', $Tail.ToString())
if ($Follow) { $argv += '-f' }
$argv += $Cfg.Docker.PgContainer

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    Invoke-HartNative -FilePath 'docker' -Argv $argv
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
