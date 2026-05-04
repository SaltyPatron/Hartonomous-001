#requires -Version 7
<#
.SYNOPSIS
  Run sql/tests/brain_4d_tests.sql against the live hartonomous DB.

.DESCRIPTION
  Self-contained substrate-side tests for substrate.dist_4d / neighborhood /
  intersect / recall. Wraps the suite in a transaction; ROLLBACK at the end
  leaves the substrate untouched. Exits non-zero on any RAISE EXCEPTION.

.EXAMPLE
  pwsh scripts/test/Brain.ps1
#>
[CmdletBinding()]
param(
    [string]$Connection = "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous"
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

$testFiles = @(
    "sql/tests/brain_4d_tests.sql",
    "sql/tests/geom_4d_tests.sql",
    "sql/tests/schema_completeness_tests.sql"
)
foreach ($f in $testFiles) {
    if (-not (Test-Path $f)) {
        Write-Host "ERROR: $f not found" -ForegroundColor Red
        exit 2
    }
}

# Parse the connection string into psql args.
$kv = @{}
foreach ($pair in $Connection.Split(';')) {
    if ($pair -match '^\s*([^=]+)\s*=\s*(.*?)\s*$') {
        $kv[$Matches[1]] = $Matches[2]
    }
}

$env:PGPASSWORD = $kv['Password']
$psql = "${env:ProgramFiles}\PostgreSQL\18\bin\psql.exe"
if (-not (Test-Path $psql)) { $psql = 'psql' }

$totalEc = 0
foreach ($f in $testFiles) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($f)
    Write-Host "==== $name ====" -ForegroundColor Cyan
    & $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] -v ON_ERROR_STOP=1 -f $f
    $ec = $LASTEXITCODE
    if ($ec -eq 0) {
        Write-Host "==== $name PASSED ====" -ForegroundColor Green
    } else {
        Write-Host "==== $name FAILED (exit $ec) ====" -ForegroundColor Red
        $totalEc = $ec
    }
}
exit $totalEc
