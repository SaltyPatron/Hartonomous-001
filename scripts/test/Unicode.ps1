#requires -Version 7
<#
.SYNOPSIS
  Run the tier-0 codepoint atom + UAX #29 determinism gate against the
  bootstrapped substrate.

.DESCRIPTION
  Two test files:
    1. sql/tests/text_decompose_determinism.sql — sanity checks on the
       extension surface (cp_hash round-trip, UAX #29 property sentinels,
       sample text_decompose call).
    2. ext/hartonomous_pg/test/test_text_decompose_determinism.cc — full
       UCD GraphemeBreakTest.txt + WordBreakTest.txt corpus conformance,
       ~1500 cases each. Run as pg_regress under the extension's
       installcheck (separate path; this script only does the SQL gate).

  Substrate must be bootstrapped + extension installed before this runs.

.PARAMETER Connection
  Npgsql connection string. Default: container defaults.
#>
[CmdletBinding()]
param(
    [string]$Connection = "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous"
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

$kv = @{}
foreach ($pair in $Connection.Split(';')) {
    if ($pair -match '^\s*([^=]+)\s*=\s*(.*?)\s*$') {
        $kv[$Matches[1]] = $Matches[2]
    }
}
$env:PGPASSWORD = $kv['Password']
$psql = "${env:ProgramFiles}\PostgreSQL\18\bin\psql.exe"
if (-not (Test-Path $psql)) { $psql = 'psql' }

$f = "sql/tests/text_decompose_determinism.sql"
if (-not (Test-Path $f)) {
    Write-Host "ERROR: $f not found" -ForegroundColor Red
    exit 2
}

Write-Host "==== text_decompose_determinism ====" -ForegroundColor Cyan
& $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] -v ON_ERROR_STOP=1 -f $f
$ec = $LASTEXITCODE
if ($ec -eq 0) {
    Write-Host "==== text_decompose_determinism PASSED ====" -ForegroundColor Green
} else {
    Write-Host "==== text_decompose_determinism FAILED (exit $ec) ====" -ForegroundColor Red
}
exit $ec
