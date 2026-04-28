#!/usr/bin/env pwsh
# Run the hartonomous PG extension's pg_regress test against the live container.
# Exits non-zero if the regression diffs.
$ErrorActionPreference = 'Stop'
$container = 'hartonomous-postgres'

# Ship the test files into the container.
docker cp ext/hartonomous_pg/test/sql/hartonomous_test.sql ${container}:/tmp/hartonomous_test.sql | Out-Null
docker exec $container bash -lc "mkdir -p /tmp/regress/test/sql /tmp/regress/test/expected; cp /tmp/hartonomous_test.sql /tmp/regress/test/sql/" | Out-Null
docker cp ext/hartonomous_pg/test/expected/hartonomous_test.out ${container}:/tmp/regress/test/expected/hartonomous_test.out | Out-Null

# Use a unique DB name per run so we don't collide with prior runs.
$dbname = "regress_$(Get-Random -Maximum 99999)"
$cmd = @"
cd /tmp/regress && /opt/pg18/lib/pgxs/src/test/regress/pg_regress \
    --inputdir=test \
    --bindir=/opt/pg18/bin \
    --user=hartonomous \
    --dbname=$dbname \
    hartonomous_test 2>&1
"@
$out = docker exec $container bash -lc $cmd
$out | Write-Host
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n--- regression.diffs ---" -ForegroundColor Yellow
    docker exec $container cat /tmp/regress/regression.diffs
    exit 1
}
