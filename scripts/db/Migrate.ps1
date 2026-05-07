#requires -Version 7
<#
.SYNOPSIS
  Deprecated. Pre-v1 substrate is bootstrap-only — there is no migration
  ledger and no incremental migration runner.

.DESCRIPTION
  This script is retained as a deprecation redirect. The bootstrap apply
  path is:

      pwsh scripts/db/Bootstrap.ps1

  …which installs the generated hartonomous PostgreSQL extension. The
  canonical source still lives under sql/schema/; build/ExtensionSql.ps1
  expands sql/schema/bootstrap.sql into ext/hartonomous_pg/sql/
  hartonomous--1.0.sql before the extension is built or staged. Pre-v1
  means drop + create + bootstrap is the workflow; edit canonical files
  in place; rebuild the extension SQL; reseed re-applies them.

  The legacy migrations directory was retired to
  sql/migrations.archive/_v2_pre_bootstrap/.

  When the substrate ships v1 and starts maintaining a deployed history,
  stage migrations re-enter the picture — not before.
#>
[CmdletBinding()]
param(
    [string]$Action,
    [int]$Target,
    [switch]$NoBuild
)

Write-Host '==== scripts/db/Migrate.ps1 is deprecated ====' -ForegroundColor Yellow
Write-Host 'Pre-v1 substrate is bootstrap-only.' -ForegroundColor Yellow
Write-Host 'Run scripts/db/Bootstrap.ps1 instead.'  -ForegroundColor Yellow
Write-Host ''
Write-Host 'Forwarding to Bootstrap.ps1...'         -ForegroundColor Cyan
Write-Host ''

& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
exit $LASTEXITCODE
