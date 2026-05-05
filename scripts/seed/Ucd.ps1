#requires -Version 7
<#
.SYNOPSIS
  Run the UcdUca seed phase entirely from the embedded extension catalog.

.DESCRIPTION
  Pure substrate-function pipeline — no C# UCD decomposer in the hot path.
  All five steps below resolve to one C call apiece into the embedded UCD
  17.0.0 tables baked into hartonomous.dll at build time.

    1. Verify substrate.ucd_version() — confirms extension is loaded with
       the expected UCD version stamp.
    2. populate_general_categories_from_ext()  — reference table.
    3. populate_scripts_from_ext()              — reference table.
    4. populate_blocks_from_ext()               — reference table; ranges
       derived via aggregation over the 1.1M-row bulk SRF.
    5. populate_break_properties_from_ext()     — reference table.
    6. populate_codepoint_property_from_ext()   — junction; replaces the
       per-codepoint round-trip from the prior C# decomposer.
    7. populate_codepoint_atoms()               — substrate.entity +
       physicality + significance for the 1,114,112 tier-0 atoms.

  -UseLegacyDecomposer falls back to the C# UcdUcaDecomposer (slow path)
  for parity validation against the extension's output.

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
    [switch]$UseLegacyDecomposer,
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
        [int]$ChunkSize = 8192
    )

    $maxCp = 1114112
    $chunk = 0
    for ($lo = 0; $lo -lt $maxCp; $lo += $ChunkSize) {
        $hi = [Math]::Min($lo + $ChunkSize, $maxCp)
        $sql = [string]::Format($SqlTemplate, $lo, $hi)
        $null = Invoke-Psql -Sql $sql -Label "$StepLabel chunk [$lo,$hi)"
        $chunk += 1
        if (($chunk % 4) -eq 0 -or $hi -eq $maxCp) {
            Write-HartInfo "  $StepLabel progress: [$lo,$hi)"
        }
    }
}

try {
    Invoke-HartStep -Name 'Verify hartonomous extension UCD version' -Action {
        $version = Invoke-Psql -Sql 'SELECT substrate.ucd_version()' -Label 'ucd_version()'
        if (-not $version) { throw "ucd_version() returned empty — extension not loaded?" }
        Write-HartInfo "  extension UCD version: $version"
    }

    if ($UseLegacyDecomposer) {
        # Parity-validation path: source UCD/UCA files still required.
        Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Ucd) -Label 'UCD XML'
        Assert-HartPath -Path (Join-Path $SourceRoot $Cfg.Seed.Uca) -Label 'UCA allkeys.txt'
        Invoke-HartStep -Name 'Phase: UcdUca (legacy C# decomposer — for parity validation)' -Action {
            Invoke-HartPhase -Cfg $Cfg -Phase 'UcdUca' -SourceRoot $SourceRoot -SkipDeps:$SkipDeps -NoBuild:$NoBuild
        }
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
    Invoke-HartStep -Name 'substrate.populate_codepoint_property_from_ext()' -Action {
        # Uses the SQL function directly — it builds proper temp lookup tables joining
        # on (code, category) so WB/SB/LB break-property IDs are resolved correctly
        # regardless of serial assignment order.
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_codepoint_property_from_ext()' -Label 'populate_codepoint_property_from_ext'
        Write-HartInfo "  +$n codepoint_property rows"
    }

        Invoke-HartStep -Name "substrate.populate_codepoint_atoms('$ProvenanceCode')" -Action {
        # Server-side function performs all four atom inserts in one SQL call
        # using set-based INSERT...SELECT operations.
        $n = Invoke-Psql -Sql "SELECT substrate.populate_codepoint_atoms('$ProvenanceCode')" -Label 'populate_codepoint_atoms'
        Write-HartInfo "  +$n codepoint atoms processed"
        }

    # Mark UcdUca as completed in monitor.phase_status so subsequent
    # phase-runner invocations (Iso639/WordNet/UD/Wiktionary/Tatoeba)
    # see the dependency as satisfied via SequentialPhaseRunner.HydrateStatusAsync
    # and DON'T re-run the legacy C# UcdUcaDecomposer (which would conflict
    # on substrate.codepoint_property's primary key).
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
