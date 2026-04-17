#requires -Version 7
<#
.SYNOPSIS
  Restore a pg_dump/tar/plain archive into the substrate DB.

.DESCRIPTION
  Destructive: drops and recreates the database first unless -Append is set.

.PARAMETER Path
  The backup file on the host.

.PARAMETER Append
  Don't drop/recreate; assume target DB exists and apply on top.

.EXAMPLE
  pwsh scripts/db/Restore.ps1 -Path artifacts/backups/hartonomous-20260417-103000.dump
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [Parameter(Mandatory)] [string]$Path,
    [switch]$Append,
    [switch]$Force
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Restore' -Cfg $Cfg

try {
    Assert-HartPath -Path $Path -Label 'backup file'
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Container $($Cfg.Docker.PgContainer) is not running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }
    if (-not $Append) {
        if (-not $Force -and -not $PSCmdlet.ShouldProcess($Cfg.Postgres.Database, 'DROP + restore')) {
            Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Aborted.'
        }
        pwsh -File (Join-Path $PSScriptRoot 'Drop.ps1') -Force
        if ($LASTEXITCODE -ne 0) { throw "Drop.ps1 failed." }
        pwsh -File (Join-Path $PSScriptRoot 'Create.ps1')
        if ($LASTEXITCODE -ne 0) { throw "Create.ps1 failed." }
    }

    $containerPath = "/tmp/restore.bin"
    Invoke-HartStep -Name "Copy backup into container" -Action {
        & docker cp $Path "$($Cfg.Docker.PgContainer):$containerPath"
        if ($LASTEXITCODE -ne 0) { throw "docker cp failed." }
    }

    $ext = [System.IO.Path]::GetExtension($Path).TrimStart('.').ToLowerInvariant()
    $usePlain = ($ext -eq 'sql')

    Invoke-HartStep -Name "Restore via $(if ($usePlain) {'psql -f'} else {'pg_restore'})" -Action {
        if ($usePlain) {
            Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv @(
                'psql','-U',$Cfg.Postgres.User,'-d',$Cfg.Postgres.Database,'-v','ON_ERROR_STOP=1','-f',$containerPath)
        } else {
            Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv @(
                'pg_restore','-U',$Cfg.Postgres.User,'-d',$Cfg.Postgres.Database,'--no-owner','--no-privileges',$containerPath)
        }
    }

    Invoke-HartContainerExec -Container $Cfg.Docker.PgContainer -Argv @('rm','-f',$containerPath) | Out-Null
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Restore complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
