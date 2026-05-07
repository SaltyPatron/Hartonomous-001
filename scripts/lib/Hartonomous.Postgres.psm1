# Hartonomous.Postgres — psql wrappers + health checks.
# Runs psql inside the compose container by default; callers that need to reach
# an external Postgres can pass -UseHostPsql to go through the local binary.

function Invoke-HartPsql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory)] [string]$Sql,
        [string]$Database,
        [switch]$UseHostPsql,
        [switch]$NoEcho
    )
    if (-not $Database) { $Database = $Cfg.Postgres.Database }
    $flags = @('-v', 'ON_ERROR_STOP=1', '-At')
    if (-not $NoEcho) { Write-HartDebug "psql($Database): $Sql" }

    if ($UseHostPsql) {
        $env:PGPASSWORD = $Cfg.Postgres.Password
        try {
            $out = & psql `
                -h $Cfg.Postgres.Host -p $Cfg.Postgres.Port `
                -U $Cfg.Postgres.User -d $Database `
                @flags -c $Sql
        }
        finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
    }
    else {
        $out = & docker exec $Cfg.Docker.PgContainer psql `
            -U $Cfg.Postgres.User -d $Database `
            @flags -c $Sql
    }
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (exit $LASTEXITCODE): $Sql"
    }
    return $out
}

function Invoke-HartPsqlScalar {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory)] [string]$Sql,
        [string]$Database,
        [switch]$UseHostPsql
    )
    $out = Invoke-HartPsql -Cfg $Cfg -Sql $Sql -Database $Database -UseHostPsql:$UseHostPsql -NoEcho
    return ($out | Select-Object -First 1)
}

function Invoke-HartPsqlFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory)] [string]$FilePath,
        [string]$Database,
        [switch]$UseHostPsql
    )
    if (-not $Database) { $Database = $Cfg.Postgres.Database }

    if ($UseHostPsql) {
        $env:PGPASSWORD = $Cfg.Postgres.Password
        try {
            & psql -h $Cfg.Postgres.Host -p $Cfg.Postgres.Port `
                   -U $Cfg.Postgres.User -d $Database `
                   -v ON_ERROR_STOP=1 -f $FilePath
        }
        finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
    }
    else {
        # Stream file into the container's psql via stdin (no mount required).
        Get-Content -Raw $FilePath |
            & docker exec -i $Cfg.Docker.PgContainer psql `
                -U $Cfg.Postgres.User -d $Database -v ON_ERROR_STOP=1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "psql -f failed (exit $LASTEXITCODE): $FilePath"
    }
}

function Test-HartDatabaseExists {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [string]$Name
    )
    if (-not $Name) { $Name = $Cfg.Postgres.Database }
    $sql = "SELECT 1 FROM pg_database WHERE datname='$Name'"
    $out = Invoke-HartPsqlScalar -Cfg $Cfg -Sql $sql -Database $Cfg.Postgres.MaintenanceDatabase
    return ($out -eq '1')
}

function Test-HartPostgisEnabled {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    $out = Invoke-HartPsqlScalar -Cfg $Cfg -Sql "SELECT 1 FROM pg_extension WHERE extname='postgis'"
    return ($out -eq '1')
}

function Test-HartHartonomousExtensionInstalled {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg)
    $out = Invoke-HartPsqlScalar -Cfg $Cfg -Sql "SELECT 1 FROM pg_extension WHERE extname='hartonomous'"
    return ($out -eq '1')
}

function Get-HartSubstrateCounts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [switch]$IncludeHeavy
    )
    $byType = @"
SELECT COUNT(DISTINCT ec.entity_hash) FROM substrate.entity_classification ec
JOIN substrate.entity_type t ON t.id = ec.entity_type_id
WHERE t.code = '{0}'
"@
    $queries = [ordered]@{
        'codepoint_property'          = 'SELECT COUNT(*) FROM substrate.codepoint_property'
        'codepoint_property.ccc>0'    = 'SELECT COUNT(*) FROM substrate.codepoint_property WHERE ccc > 0'
        'codepoint_property.casefold' = 'SELECT COUNT(*) FROM substrate.codepoint_property WHERE full_case_fold IS NOT NULL'
        'codepoint_property.decomp'   = 'SELECT COUNT(*) FROM substrate.codepoint_property WHERE decomposition_mapping IS NOT NULL'
        'codepoint_property.extpict'  = 'SELECT COUNT(*) FROM substrate.codepoint_property WHERE is_extended_pictographic'
        'language'                    = 'SELECT COUNT(*) FROM substrate.language'
        'entity (synsets)'            = $byType -f 'synset'
        'entity (lemmas)'             = $byType -f 'lemma'
        'entity (codepoints)'         = $byType -f 'codepoint'
        'entity (total)'              = 'SELECT COUNT(*) FROM substrate.entity'
        'entity_classification'       = 'SELECT COUNT(*) FROM substrate.entity_classification'
        'edge (total)'                = 'SELECT COUNT(*) FROM substrate.edge'
        'edge_member (total)'         = 'SELECT COUNT(*) FROM substrate.edge_member'
        'edge (geom null)'            = 'SELECT COUNT(*) FROM substrate.edge WHERE geom IS NULL'
        'physicality (total)'         = 'SELECT COUNT(*) FROM substrate.physicality'
    }
    if ($IncludeHeavy) {
        $queries['sequence (total)'] = 'SELECT COUNT(*) FROM substrate.sequence'
        $queries['entity_significance'] = 'SELECT COUNT(*) FROM substrate.entity_significance'
        $queries['edge_significance'] = 'SELECT COUNT(*) FROM substrate.edge_significance'
    }
    $result = [ordered]@{}
    foreach ($kv in $queries.GetEnumerator()) {
        try   { $result[$kv.Key] = Invoke-HartPsqlScalar -Cfg $Cfg -Sql $kv.Value }
        catch { $result[$kv.Key] = '(unavailable)' }
    }
    return $result
}

Export-ModuleMember -Function `
    Invoke-HartPsql, Invoke-HartPsqlScalar, Invoke-HartPsqlFile, `
    Test-HartDatabaseExists, Test-HartPostgisEnabled, Test-HartHartonomousExtensionInstalled, `
    Get-HartSubstrateCounts
