#requires -Version 7
<#
.SYNOPSIS
  Full substrate dashboard: daemon, container, DB, extension, migration HEAD,
  phase status, and row counts.

.EXAMPLE
  pwsh scripts/ops/Status.ps1
#>
[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"   -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'ops.Status' -Cfg $Cfg

try {
    Write-HartBanner 'Hartonomous status'

    # Docker
    $daemon = if (Test-HartDockerDaemon) { 'up' } else { 'down' }
    "{0,-22} {1}" -f 'Docker daemon:', $daemon | Write-Host
    $containerState = Get-HartContainerHealth -Name $Cfg.Docker.PgContainer
    "{0,-22} {1}  [{2}]" -f 'Container:', $Cfg.Docker.PgContainer, $containerState | Write-Host

    if ($containerState -in 'healthy','unknown','starting','unhealthy') {
        # Database
        $dbOk = Test-HartDatabaseExists -Cfg $Cfg
        "{0,-22} {1}" -f 'Database exists:', $dbOk | Write-Host
        if ($dbOk) {
            "{0,-22} {1}" -f 'PostGIS:',   (Test-HartPostgisEnabled -Cfg $Cfg)                | Write-Host
            "{0,-22} {1}" -f 'hartonomous ext:', (Test-HartHartonomousExtensionInstalled -Cfg $Cfg) | Write-Host

            # Migration HEAD
            try {
                $head = Invoke-HartPsqlScalar -Cfg $Cfg -Sql 'SELECT version FROM substrate.schema_version ORDER BY version DESC LIMIT 1'
                "{0,-22} {1}" -f 'Migration HEAD:', $head | Write-Host
            } catch {
                "{0,-22} {1}" -f 'Migration HEAD:', '(substrate.schema_version missing)' | Write-Host
            }

            # Row counts summary
            Write-HartBanner 'Substrate counts'
            $counts = Get-HartSubstrateCounts -Cfg $Cfg
            foreach ($kv in $counts.GetEnumerator()) { '{0,-32} {1,10}' -f $kv.Key, $kv.Value | Write-Host }
        }
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
