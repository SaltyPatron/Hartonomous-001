#requires -Version 7
<#
.SYNOPSIS
  Run the UcdUca seed phase entirely from the embedded extension catalog.

.DESCRIPTION
    Pure substrate-function pipeline — no C# UCD decomposer in the hot path.
    The database is still seeded. The embedded UCD/UCA static data is the source
    for fast deterministic loads into substrate tables; the runtime embedded
    cache is a complementary lookup surface, not a replacement for DB state.

    1. Verify substrate.ucd_version() — confirms extension is loaded with
       the expected UCD version stamp.
    2. populate_general_categories_from_ext()  — reference table.
     3. populate_scripts_from_ext()              — reference table.
     4. populate_blocks_from_ext()               — reference table; ranges
         emitted directly by the generated inventory.
    5. populate_break_properties_from_ext()     — reference table.
     6. populate_codepoint_property_range_from_ext() in client-side chunks —
         junction; full DB seed from generated static UCD/UCA arrays without
         a monolithic server-side statement.
    7. populate_codepoint_atoms()               — substrate.entity +
       physicality + significance for the 1,114,112 tier-0 atoms.

.PARAMETER SourceRoot
  Root containing UCD/Public/UCD/latest. Default from config.psd1.

.PARAMETER ProvenanceCode
  Provenance to credit on populate_codepoint_atoms verification.

.EXAMPLE
  pwsh scripts/seed/Ucd.ps1
  pwsh scripts/seed/Ucd.ps1 -SourceRoot D:\Models
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [switch]$SkipDeps,
    [switch]$NoBuild,
    [string]$ProvenanceCode = 'unicode_consortium',
    [string]$Connection = "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous"
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Phases.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
Start-HartonomousLog -ScriptName 'seed.Ucd' -Cfg $Cfg

$kv = @{}
foreach ($pair in $Connection.Split(';')) {
    if ($pair -match '^\s*([^=]+)\s*=\s*(.*?)\s*$') {
        $kv[$Matches[1]] = $Matches[2]
    }
}
$env:PGPASSWORD = $kv['Password']
# Force UTF8 client encoding so cp_* text values are decoded consistently across host/client platforms.
$env:PGCLIENTENCODING = 'UTF8'
$psql = "${env:ProgramFiles}\PostgreSQL\18\bin\psql.exe"
if (-not (Test-Path $psql)) { $psql = 'psql' }

$useDockerPsql = $false
if (($kv['Host'] -eq 'localhost' -or $kv['Host'] -eq '127.0.0.1') -and $kv['Port'] -eq '5433') {
    try {
        $containerName = (& docker ps --format "{{.Names}}" 2>$null | Where-Object { $_ -eq 'hartonomous-postgres' } | Select-Object -First 1)
        $useDockerPsql = -not [string]::IsNullOrWhiteSpace($containerName)
    }
    catch {
        $useDockerPsql = $false
    }
}

function Invoke-Psql {
    param([string]$Sql, [string]$Label)

    if ($useDockerPsql) {
        $out = & docker exec -e "PGPASSWORD=$($kv['Password'])" hartonomous-postgres `
            psql -U $kv['Username'] -d $kv['Database'] -v ON_ERROR_STOP=1 -t -A -c $Sql 2>&1
    }
    else {
        $out = & $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] `
            -v ON_ERROR_STOP=1 -t -A -c $Sql 2>&1
    }

    if ($LASTEXITCODE -ne 0) { throw "${Label}: $out" }
    return ($out -join "`n").Trim()
}

function Invoke-ChunkedCodepointSql {
    param(
        [Parameter(Mandatory = $true)][string]$StepLabel,
        [Parameter(Mandatory = $true)][string]$SqlTemplate,
        [int]$ChunkSize = 32768
    )

    $maxCp = 1114112
    $chunk = 0
    [int64]$total = 0
    for ($lo = 0; $lo -lt $maxCp; $lo += $ChunkSize) {
        $hi = [Math]::Min($lo + $ChunkSize, $maxCp)
        $sql = [string]::Format($SqlTemplate, $lo, $hi)
        $result = Invoke-Psql -Sql $sql -Label "$StepLabel chunk [$lo,$hi)"
        if (-not [string]::IsNullOrWhiteSpace($result)) {
            $total += [int64]$result
        }
        $chunk += 1
        Write-HartInfo "  $StepLabel progress: [$lo,$hi)"
    }
    return $total
}

try {
    Invoke-HartStep -Name 'Verify hartonomous extension UCD version' -Action {
        $version = Invoke-Psql -Sql 'SELECT substrate.ucd_version()' -Label 'ucd_version()'
        if (-not $version) { throw "ucd_version() returned empty — extension not loaded?" }
        Write-HartInfo "  extension UCD version: $version"
    }

    Invoke-HartStep -Name 'substrate.populate_general_categories_from_ext()' -Action {
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_general_categories_from_ext()' -Label 'populate_general_categories_from_ext'
        Write-HartInfo "  +$n general_category rows"
    }
    Invoke-HartStep -Name 'substrate.populate_scripts_from_ext()' -Action {
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_scripts_from_ext()' -Label 'populate_scripts_from_ext'
        Write-HartInfo "  +$n script rows"
    }
    Invoke-HartStep -Name 'substrate.populate_blocks_from_ext()' -Action {
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_blocks_from_ext()' -Label 'populate_blocks_from_ext'
        Write-HartInfo "  +$n block rows"
    }
    Invoke-HartStep -Name 'substrate.populate_break_properties_from_ext()' -Action {
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_break_properties_from_ext()' -Label 'populate_break_properties_from_ext'
        Write-HartInfo "  +$n break_property rows"
    }
    Invoke-HartStep -Name 'substrate.populate_codepoint_property_range_from_ext() chunks' -Action {
        # Full database seed from generated static UCD/UCA arrays. Client-side
        # chunks keep real statement boundaries while the range function uses
        # set-based INSERT...SELECT and leaves normal FK checks active.
        $n = Invoke-ChunkedCodepointSql `
            -StepLabel 'codepoint_property' `
            -SqlTemplate 'SELECT substrate.populate_codepoint_property_range_from_ext({0}, {1} - {0})' `
            -ChunkSize 32768
        Write-HartInfo "  +$n codepoint_property rows"
    }

    Invoke-HartStep -Name "substrate.populate_codepoint_atoms_chunk × 8 parallel" -Action {
        # Parallel UCD seed: split 1,114,112 codepoints into 8 disjoint
        # ranges and run populate_codepoint_atoms_chunk concurrently across
        # 8 PG backends. Replaces the previous single-backend call which
        # pinned UCD seed to one core for tens of minutes (5% CPU on a
        # 24-core 14900KS host).
        #
        # Each parallel branch opens its own docker exec / psql connection.
        # PG-side concurrency is bounded by the docker-compose
        # max_connections=50 setting; 8 backends well within that.
        $maxCp = 1114112
        # 8-way parallel chunking. The earlier SIGSEGV in the parallel SRF
        # was root-caused to substrate.ucd_codepoints emitting tuples for
        # codepoints whose per-block UCD blob was unmapped; the C-side
        # block-presence guard in pg_codepoint_atoms_pg.c::ucd_atom_setof
        # fixes that. The `degree=1` workaround that replaced parallelism
        # with a single 1.1M-row monolithic call was actively harmful: it
        # blew past PG's transaction memory ceiling, crashed mid-INSERT,
        # left WAL in an inconsistent state, and PANICed recovery on a
        # btree_xlog_insert assertion (core wsl-crash-1778292957). Eight
        # backends × 139k codepoints each is what the schema was sized for.
        $degree = 8
        $chunkSize = [Math]::Ceiling($maxCp / $degree)
        $ranges = @()
        for ($lo = 0; $lo -lt $maxCp; $lo += $chunkSize) {
            $hi = [Math]::Min($lo + $chunkSize, $maxCp)
            $ranges += [pscustomobject]@{ Lo = $lo; Hi = $hi }
        }
        Write-HartInfo "  parallelism=$degree, ranges:"
        foreach ($r in $ranges) { Write-HartInfo "    [$($r.Lo),$($r.Hi))" }

        $useDocker = $useDockerPsql
        $usr = $kv['Username']
        $pwd = $kv['Password']
        $db = $kv['Database']
        $hst = $kv['Host']
        $prt = $kv['Port']
        $localPsql = $psql
        $prov = $ProvenanceCode

        # ForEach-Object -Parallel runs each range in its own runspace.
        $results = $ranges | ForEach-Object -Parallel {
            $r = $_
            # Explicit type casts — Postgres types bare NULL as unknown and
            # cannot resolve the (TEXT, FLOAT8, INT, INT) overload without
            # them, even though 'unicode_consortium' would auto-cast on its
            # own. Without ::float8 on NULL the call fails with
            # "function substrate.populate_codepoint_atoms_chunk(unknown,
            # unknown, integer, integer) does not exist".
            $sql = "SELECT substrate.populate_codepoint_atoms_chunk('$using:prov'::text, NULL::float8, $($r.Lo)::int, $($r.Hi)::int)"
            if ($using:useDocker) {
                $out = & docker exec -e "PGPASSWORD=$using:pwd" hartonomous-postgres `
                    psql -U $using:usr -d $using:db -v ON_ERROR_STOP=1 -t -A -c $sql 2>&1
            }
            else {
                $env:PGPASSWORD = $using:pwd
                $out = & $using:localPsql -h $using:hst -p $using:prt -U $using:usr -d $using:db `
                    -v ON_ERROR_STOP=1 -t -A -c $sql 2>&1
            }
            if ($LASTEXITCODE -ne 0) { throw "populate_codepoint_atoms_chunk [$($r.Lo),$($r.Hi)): $out" }
            [pscustomobject]@{ Lo = $r.Lo; Hi = $r.Hi; Count = ($out -join "`n").Trim() }
        } -ThrottleLimit $degree

        [int64]$total = 0
        foreach ($res in $results) {
            if (-not [string]::IsNullOrWhiteSpace($res.Count)) {
                $total += [int64]$res.Count
            }
        }
        Write-HartInfo "  +$total codepoint atoms processed across $degree parallel backends"
    }

    # Mark UcdUca as completed in monitor.phase_status so subsequent
    # phase-runner invocations (Iso639/WordNet/UD/Wiktionary/Tatoeba)
    # see the dependency as satisfied via SequentialPhaseRunner.HydrateStatusAsync.
    Invoke-HartStep -Name 'Mark UcdUca completed in monitor.phase_status' -Action {
        $null = Invoke-Psql -Sql "CALL monitor.update_phase_status('UcdUca', 'completed', NULL)" -Label 'monitor.update_phase_status'
        Write-HartInfo '  UcdUca = completed (substrate-side)'
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'UcdUca seeded entirely from embedded extension catalog.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
