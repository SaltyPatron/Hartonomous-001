#requires -Version 7
<#
.SYNOPSIS
  Execute an arbitrary command inside the Postgres container.

.EXAMPLE
  pwsh scripts/docker/Exec.ps1 -Command 'bash -lc "ls /usr/lib/postgresql/17/lib/hartonomous.so"'
  pwsh scripts/docker/Exec.ps1 -- apt list --installed
#>
[CmdletBinding()]
param(
    [string]$Container,
    [Parameter(ValueFromRemainingArguments)] [string[]]$Command
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'docker.Exec' -Cfg $Cfg

if (-not $Container) { $Container = $Cfg.Docker.PgContainer }
if (-not $Command -or $Command.Count -eq 0) {
    Write-HartError 'No command provided.'
    Exit-Hartonomous -Code $Cfg.ExitCodes.Usage
}

try {
    Assert-HartCommand -Name 'docker' | Out-Null
    Invoke-HartContainerExec -Container $Container -Argv $Command
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
