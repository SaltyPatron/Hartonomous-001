#requires -Version 7
<#
.SYNOPSIS
  pg_dump the substrate DB to a timestamped file under artifacts/backups/.

.PARAMETER Format
  'c' (custom, default, compressed) | 'p' (plain SQL) | 't' (tar).

.PARAMETER OutDir
  Override the destination directory.

.EXAMPLE
  pwsh scripts/db/Backup.ps1
  pwsh scripts/db/Backup.ps1 -Format p -OutDir D:\backups\hartonomous
#>
[CmdletBinding()]
param(
    [ValidateSet('c','p','t')] [string]$Format = 'c',
    [string]$OutDir
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Backup' -Cfg $Cfg

try {
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }

    if (-not $OutDir) { $OutDir = Join-Path $Cfg.Repo.Root (Join-Path $Cfg.Paths.Artifacts 'backups') }
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ext = switch ($Format) { 'c' {'dump'}; 'p' {'sql'}; 't' {'tar'} }
    $host_ = "$($Cfg.Postgres.Database)-$stamp.$ext"
    $containerPath = "/tmp/$host_"
    $hostPath = Join-Path $OutDir $host_

    Invoke-HartStep -Name "pg_dump ($Format) → $hostPath" -Action {
        $argv = @('-U', $Cfg.Postgres.User, '-F', $Format, '-f', $containerPath, $Cfg.Postgres.Database)
        Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv (@('pg_dump') + $argv)
        & docker cp "$($Cfg.Docker.PgContainer):$containerPath" $hostPath
        if ($LASTEXITCODE -ne 0) { throw "docker cp failed." }
        Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv @('rm','-f',$containerPath) | Out-Null
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "Backup: $hostPath"
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
