#requires -Version 7
<#
.SYNOPSIS
    Report where the substrate stands right now: infrastructure, data, phases,
    significance, geometry, and core query/function readiness. Any FAIL or WARN
    exits non-zero; warnings are unresolved work, not accepted completion.

.PARAMETER RunSqlTests
  Also run non-mutating SQL validation files: schema completeness and 4D
  geometry coverage. This is slower than the default probe set.

.EXAMPLE
  pwsh scripts/ops/Readiness.ps1
  pwsh scripts/ops/Readiness.ps1 -RunSqlTests
#>
[CmdletBinding()]
param(
    [switch]$RunSqlTests
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1"   -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Postgres.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Verify.psm1"   -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'ops.Readiness' -Cfg $Cfg

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [Parameter(Mandatory)] [string]$Area,
        [Parameter(Mandatory)] [ValidateSet('PASS','WARN','FAIL','SKIP')] [string]$Status,
        [Parameter(Mandatory)] [string]$Detail
    )
    $script:results.Add([pscustomobject]@{
        Area = $Area
        Status = $Status
        Detail = $Detail
    }) | Out-Null
}

function Get-ScalarOrNull {
    param([Parameter(Mandatory)] [string]$Sql)
    try { return Invoke-HartPsqlScalar -Cfg $Cfg -Sql $Sql }
    catch { return $null }
}

function Write-ResultTable {
    Write-HartBanner 'Readiness gates'
    foreach ($result in $script:results) {
        '{0,-30} {1,-5} {2}' -f $result.Area, $result.Status, $result.Detail | Write-Host
    }
}

try {
    Write-HartBanner 'Hartonomous readiness'

    $daemonOk = Test-HartDockerDaemon
    Add-Result 'Docker daemon' ($(if ($daemonOk) { 'PASS' } else { 'FAIL' })) ($(if ($daemonOk) { 'daemon reachable' } else { 'daemon not reachable' }))
    if (-not $daemonOk) {
        Write-ResultTable
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable -Message 'Docker daemon unavailable.'
    }

    $containerHealth = Get-HartContainerHealth -Name $Cfg.Docker.PgContainer
    Add-Result 'Postgres container' ($(if ($containerHealth -eq 'healthy') { 'PASS' } elseif ($containerHealth -eq 'missing') { 'FAIL' } else { 'WARN' })) "$($Cfg.Docker.PgContainer) [$containerHealth]"
    if ($containerHealth -eq 'missing') {
        Write-ResultTable
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable -Message 'Postgres container missing.'
    }

    $dbOk = Test-HartDatabaseExists -Cfg $Cfg
    Add-Result 'Database' ($(if ($dbOk) { 'PASS' } else { 'FAIL' })) ($(if ($dbOk) { $Cfg.Postgres.Database } else { 'database not found' }))
    if (-not $dbOk) {
        Write-ResultTable
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable -Message 'Database unavailable.'
    }

    $postgisOk = Test-HartPostgisEnabled -Cfg $Cfg
    $hartOk = Test-HartHartonomousExtensionInstalled -Cfg $Cfg
    $hartVersion = Get-ScalarOrNull "SELECT extversion FROM pg_extension WHERE extname='hartonomous'"
    Add-Result 'PostGIS extension' ($(if ($postgisOk) { 'PASS' } else { 'FAIL' })) ($(if ($postgisOk) { 'installed' } else { 'missing' }))
    Add-Result 'hartonomous extension' ($(if ($hartOk) { 'PASS' } else { 'FAIL' })) ($(if ($hartOk) { "installed version $hartVersion" } else { 'missing' }))

    $healthRows = Get-ScalarOrNull 'SELECT count(*) FROM substrate.health_summary()'
    Add-Result 'health_summary function' ($(if ([int]$healthRows -gt 0) { 'PASS' } else { 'FAIL' })) "rows returned: $healthRows"

    $requiredFunctions = @(
        'health_summary', 'text_decompose', 'recompose_text', 'infer', 'infer_topk',
        'recall', 'populate_edge_trajectories', 'prime_unprimed_edges_chunk'
    )
    $functionList = ($requiredFunctions | ForEach-Object { "'$_'" }) -join ','
    $functionCount = Get-ScalarOrNull "SELECT count(DISTINCT p.proname) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'substrate' AND p.proname IN ($functionList)"
    Add-Result 'Core query functions' ($(if ([int]$functionCount -eq $requiredFunctions.Count) { 'PASS' } else { 'WARN' })) "$functionCount/$($requiredFunctions.Count) expected functions present"

    $inlineSqlFindings = @(Get-HartInlineSqlFindings -Cfg $Cfg -Scope CSharp)
    Add-Result 'C# inline SQL' ($(if ($inlineSqlFindings.Count -eq 0) { 'PASS' } else { 'FAIL' })) "$($inlineSqlFindings.Count) candidate SQL literal(s) under src/"

    $dist4d = Get-ScalarOrNull "SELECT substrate.dist_4d(ST_MakePoint(0,0,0,0)::geometry, ST_MakePoint(1,1,1,1)::geometry)"
    $distOk = $false
    if ($null -ne $dist4d) { $distOk = [math]::Abs([double]$dist4d - 2.0) -lt 0.000000001 }
    Add-Result '4D distance probe' ($(if ($distOk) { 'PASS' } else { 'FAIL' })) "dist_4d unit diagonal = $dist4d"

    $counts = Get-HartSubstrateCounts -Cfg $Cfg -IncludeHeavy
    Write-HartBanner 'Substrate counts'
    foreach ($kv in $counts.GetEnumerator()) {
        '{0,-32} {1,14}' -f $kv.Key, $kv.Value | Write-Host
    }

    if ($inlineSqlFindings.Count -gt 0) {
        Write-HartBanner 'C# inline SQL candidates'
        $inlineSqlFindings | Select-Object -First 50 | ForEach-Object {
            '{0}:{1}  {2}' -f $_.Path, $_.Line, $_.Text | Write-Host
        }
        if ($inlineSqlFindings.Count -gt 50) {
            "... $($inlineSqlFindings.Count - 50) more candidate(s) omitted" | Write-Host
        }
    }

    $codepointProperties = [int64]$counts['codepoint_property']
    Add-Result 'UCD/UCA data' ($(if ($codepointProperties -eq 1114112) { 'PASS' } elseif ($codepointProperties -gt 0) { 'WARN' } else { 'FAIL' })) "codepoint_property rows: $codepointProperties"

    $languages = [int64]$counts['language']
    Add-Result 'ISO 639 data' ($(if ($languages -gt 0) { 'PASS' } else { 'FAIL' })) "language rows: $languages"

    $synsets = [int64]$counts['entity (synsets)']
    $lemmas = [int64]$counts['entity (lemmas)']
    Add-Result 'WordNet/OMW data' ($(if ($synsets -gt 0 -and $lemmas -gt 0) { 'PASS' } else { 'WARN' })) "synsets=$synsets lemmas=$lemmas"

    $edges = [int64]$counts['edge (total)']
    $edgeMembers = [int64]$counts['edge_member (total)']
    Add-Result 'Edge substrate' ($(if ($edges -gt 0 -and $edgeMembers -gt 0) { 'PASS' } else { 'WARN' })) "edges=$edges edge_members=$edgeMembers"

    $physicality = [int64]$counts['physicality (total)']
    Add-Result 'Physicality substrate' ($(if ($physicality -gt 0) { 'PASS' } else { 'WARN' })) "physicality rows: $physicality"

    $nullEdgeGeom = [int64]$counts['edge (geom null)']
    Add-Result 'Edge trajectories' ($(if ($edges -eq 0) { 'SKIP' } elseif ($nullEdgeGeom -eq 0) { 'PASS' } else { 'FAIL' })) "edges with NULL geom: $nullEdgeGeom"

    $arenaCount = [int64](Get-ScalarOrNull 'SELECT count(*) FROM substrate.significance_context')
    $edgeSigRows = [int64]$counts['edge_significance']
    $edgeSigArenas = [int64](Get-ScalarOrNull 'SELECT count(DISTINCT context_type_id) FROM substrate.edge_significance')
    $expectedEdgeSigRows = $edges * $arenaCount
    $edgeSigRowsPerArena = if ($arenaCount -gt 0) { [math]::Floor($edgeSigRows / $arenaCount) } else { 0 }
    $edgeSigStatus = if ($edges -eq 0) { 'SKIP' } elseif ($edgeSigArenas -eq $arenaCount -and $edgeSigRows -ge $expectedEdgeSigRows) { 'PASS' } else { 'FAIL' }
    Add-Result 'Edge significance' $edgeSigStatus "rows=$edgeSigRows arenas=$edgeSigArenas/$arenaCount rows_per_arena=$edgeSigRowsPerArena expected_at_least=$expectedEdgeSigRows"

    Write-HartBanner 'Entity classifications by type'
    $entityTypeRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
SELECT et.code, count(DISTINCT ec.entity_hash)
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
GROUP BY et.code
ORDER BY count(DISTINCT ec.entity_hash) DESC, et.code
LIMIT 25
"@
    foreach ($row in $entityTypeRows) {
        $parts = $row -split '\|'
        '{0,-32} {1,14}' -f $parts[0], $parts[1] | Write-Host
    }

    Write-HartBanner 'Edges by type'
    $edgeTypeRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
SELECT et.code, count(*)
FROM substrate.edge e
JOIN substrate.edge_type et ON et.id = e.edge_type_id
GROUP BY et.code
ORDER BY count(*) DESC, et.code
LIMIT 25
"@
    foreach ($row in $edgeTypeRows) {
        $parts = $row -split '\|'
        '{0,-32} {1,14}' -f $parts[0], $parts[1] | Write-Host
    }

    Write-HartBanner 'Physicality by type'
    $physicalityRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
SELECT pt.code, count(*)
FROM substrate.physicality p
JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
GROUP BY pt.code
ORDER BY count(*) DESC, pt.code
"@
    foreach ($row in $physicalityRows) {
        $parts = $row -split '\|'
        '{0,-32} {1,14}' -f $parts[0], $parts[1] | Write-Host
    }

    Write-HartBanner 'Edge significance by arena'
    $edgeSigArenaRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
SELECT sc.code, count(*) AS rows, min(es.mu), max(es.mu), max(es.games)
FROM substrate.edge_significance es
JOIN substrate.significance_context sc ON sc.id = es.context_type_id
GROUP BY sc.code
ORDER BY sc.code
"@
    foreach ($row in $edgeSigArenaRows) {
        $parts = $row -split '\|'
        '{0,-34} rows={1,-12} mu=[{2}..{3}] games_max={4}' -f $parts[0], $parts[1], $parts[2], $parts[3], $parts[4] | Write-Host
    }

    Write-HartBanner 'Edge significance gaps by type'
    $edgeSigGapRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
WITH edge_counts AS (
    SELECT edge_type_id, count(*) AS edges
    FROM substrate.edge
    GROUP BY edge_type_id
), sig_counts AS (
    SELECT edge_type_id, count(*) AS sig_rows, count(DISTINCT context_type_id) AS arenas
    FROM substrate.edge_significance
    GROUP BY edge_type_id
), arena_count AS (
    SELECT count(*)::bigint AS arenas FROM substrate.significance_context
)
SELECT et.code,
       e.edges,
       coalesce(s.sig_rows, 0) AS sig_rows,
       coalesce(s.arenas, 0) AS arenas,
       (e.edges * ac.arenas - coalesce(s.sig_rows, 0)) AS missing_rows
FROM edge_counts e
CROSS JOIN arena_count ac
JOIN substrate.edge_type et ON et.id = e.edge_type_id
LEFT JOIN sig_counts s ON s.edge_type_id = e.edge_type_id
WHERE (e.edges * ac.arenas - coalesce(s.sig_rows, 0)) > 0
ORDER BY missing_rows DESC, e.edges DESC, et.code
LIMIT 25
"@
    if ($edgeSigGapRows.Count -eq 0) {
        '(no significance gaps by edge type)' | Write-Host
    } else {
        foreach ($row in $edgeSigGapRows) {
            $parts = $row -split '\|'
            '{0,-32} edges={1,-10} sig_rows={2,-10} arenas={3,-3} missing_rows={4}' -f $parts[0], $parts[1], $parts[2], $parts[3], $parts[4] | Write-Host
        }
    }

    Write-HartBanner 'Edge trajectory gaps by type'
    $edgeGeomGapRows = Invoke-HartPsql -Cfg $Cfg -Sql @"
SELECT et.code,
       count(*) AS edges,
       count(*) FILTER (WHERE e.geom IS NULL) AS null_geom,
       count(*) FILTER (WHERE e.geom IS NOT NULL) AS with_geom
FROM substrate.edge e
JOIN substrate.edge_type et ON et.id = e.edge_type_id
GROUP BY et.code
HAVING count(*) FILTER (WHERE e.geom IS NULL) > 0
ORDER BY null_geom DESC, edges DESC, et.code
LIMIT 25
"@
    if ($edgeGeomGapRows.Count -eq 0) {
        '(no NULL edge geometries)' | Write-Host
    } else {
        foreach ($row in $edgeGeomGapRows) {
            $parts = $row -split '\|'
            '{0,-32} edges={1,-10} null_geom={2,-10} with_geom={3}' -f $parts[0], $parts[1], $parts[2], $parts[3] | Write-Host
        }
    }

    Write-HartBanner 'Phase status'
    $expectedPhases = @(
        'CoreAlgebra', 'UcdUca', 'Iso639', 'WordNetOmw', 'UniversalDeps', 'Wiktionary',
        'Tatoeba', 'TextDecomp', 'ModelDecomp', 'SignificanceField', 'InferenceEngine', 'Validation'
    )
    $phaseRows = Invoke-HartPsql -Cfg $Cfg -Sql "SELECT phase_code, status, coalesce(completed_at::text, ''), coalesce(error_message, '') FROM monitor.phase_status ORDER BY phase_code"
    if ($phaseRows.Count -eq 0) {
        '(no phase status rows)' | Write-Host
        Add-Result 'Phase status' 'WARN' 'monitor.phase_status has no rows'
    } else {
        $presentPhases = New-Object System.Collections.Generic.HashSet[string]
        $nonCompleted = New-Object System.Collections.Generic.List[string]
        foreach ($row in $phaseRows) {
            $parts = $row -split '\|'
            $presentPhases.Add($parts[0]) | Out-Null
            if ($parts[1] -ne 'completed') { $nonCompleted.Add("$($parts[0])=$($parts[1])") | Out-Null }
            '{0,-22} {1,-14} {2} {3}' -f $parts[0], $parts[1], $parts[2], $parts[3] | Write-Host
        }
        $missingPhases = $expectedPhases | Where-Object { -not $presentPhases.Contains($_) }
        if ($missingPhases.Count -gt 0) {
            Add-Result 'Phase status' 'FAIL' "missing phase rows: $($missingPhases -join ', ')"
        } elseif ($nonCompleted.Count -gt 0) {
            Add-Result 'Phase status' 'FAIL' "not completed: $($nonCompleted -join ', ')"
        } else {
            Add-Result 'Phase status' 'PASS' "$($phaseRows.Count)/$($expectedPhases.Count) phases completed"
        }
    }

    if ($RunSqlTests) {
        Write-HartBanner 'SQL validation files'
        $schemaTest = Join-Path $Cfg.Repo.Root 'sql\tests\schema_completeness_tests.sql'
        $geomTest = Join-Path $Cfg.Repo.Root 'sql\tests\geom_4d_tests.sql'
        Invoke-HartPsqlFile -Cfg $Cfg -FilePath $schemaTest | Out-Null
        Add-Result 'schema_completeness_tests' 'PASS' 'sql/tests/schema_completeness_tests.sql passed'
        Invoke-HartPsqlFile -Cfg $Cfg -FilePath $geomTest | Out-Null
        Add-Result 'geom_4d_tests' 'PASS' 'sql/tests/geom_4d_tests.sql passed'
    }

    Write-ResultTable

    $failCount = ($results | Where-Object Status -eq 'FAIL').Count
    $warnCount = ($results | Where-Object Status -eq 'WARN').Count
    if ($failCount -gt 0 -or $warnCount -gt 0) {
        Exit-Hartonomous -Code $Cfg.ExitCodes.DataError -Message "Readiness has $failCount failing gate(s) and $warnCount warning(s)."
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "Readiness complete with $warnCount warning(s)."
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
