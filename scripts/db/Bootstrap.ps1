#requires -Version 7
<#
.SYNOPSIS
  Install the hartonomous extension into the database — one psql call.

.DESCRIPTION
  The substrate is now packaged as a proper PostgreSQL extension
  (PostGIS / pgvector pattern). All schemas, types, tables, partitions,
  reference seeds, junctions, functions, views, opclasses, and C-binding
  declarations are built into a single hartonomous--1.0.sql at build time
  (via scripts/build/ExtensionSql.ps1) and shipped under PG's
  $share/extension/. Installation is then one transactional CREATE
  EXTENSION call — no per-include resolution at runtime.

  CREATE EXTENSION hartonomous auto-installs prerequisites declared in
  hartonomous.control's `requires` (postgis, btree_gist, pg_trgm).

  This script is idempotent: IF NOT EXISTS keeps re-runs safe. Use
  scripts/db/Drop.ps1 + scripts/db/Create.ps1 first for a fresh DB.

.EXAMPLE
  pwsh scripts/db/Bootstrap.ps1
#>
[CmdletBinding()]
param(
    [string]$Connection = $(if ($env:HARTONOMOUS_DB) { $env:HARTONOMOUS_DB } else { 'Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous' })
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'db.Bootstrap' -Cfg $Cfg

try {
    Invoke-HartStep -Name 'CREATE EXTENSION hartonomous (atomic install)' -Action {
        Invoke-HartPsql -Cfg $Cfg -Sql 'CREATE EXTENSION IF NOT EXISTS hartonomous CASCADE' | Out-Null
        Write-HartInfo 'extension installed.'
    }

    Invoke-HartStep -Name 'Verify substrate + monitor schemas present' -Action {
        $count = Invoke-HartPsqlScalar -Cfg $Cfg `
            -Sql "SELECT count(*) FROM pg_namespace WHERE nspname IN ('substrate','monitor')"
        if ([int]$count -lt 2) {
            throw "expected substrate + monitor schemas to exist after CREATE EXTENSION (got $count)"
        }
        Write-HartInfo "  $count expected schemas present."
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Substrate ready.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
