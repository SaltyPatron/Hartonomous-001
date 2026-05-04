#requires -Version 7
<#
.SYNOPSIS
  Deprecated. Pre-v1 substrate is bootstrap-only — there is no migration
  ledger and no incremental migration runner.

.DESCRIPTION
  This script is retained as a deprecation redirect. The bootstrap apply
  path is:

      pwsh scripts/db/Bootstrap.ps1

  …which runs sql/schema/bootstrap.sql through MigrationFileLoader and
  applies the canonical schema/ tree in one transaction. Pre-v1 means
  drop + create + bootstrap is the workflow; edit canonical files in
  place; reseed re-applies them.

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
