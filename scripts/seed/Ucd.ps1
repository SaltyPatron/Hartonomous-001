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
$psql = "${env:ProgramFiles}\PostgreSQL\18\bin\psql.exe"
if (-not (Test-Path $psql)) { $psql = 'psql' }

function Invoke-Psql {
    param([string]$Sql, [string]$Label)
    $out = & $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] `
        -v ON_ERROR_STOP=1 -t -A -c $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "${Label}: $out" }
    return ($out -join "`n").Trim()
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
    Invoke-HartStep -Name 'substrate.populate_codepoint_property_from_ext() (1,114,112-row bulk insert)' -Action {
        $n = Invoke-Psql -Sql 'SELECT substrate.populate_codepoint_property_from_ext()' -Label 'populate_codepoint_property_from_ext'
        Write-HartInfo "  +$n codepoint_property rows"
    }
    Invoke-HartStep -Name "substrate.populate_codepoint_atoms('$ProvenanceCode') — tier-0 atoms" -Action {
        $sql = "SELECT substrate.populate_codepoint_atoms('$ProvenanceCode')"
        & $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] -v ON_ERROR_STOP=1 -c $sql
        if ($LASTEXITCODE -ne 0) { throw "populate_codepoint_atoms failed (exit $LASTEXITCODE)" }
    }

    # Mark UcdUca as completed in monitor.phase_status so subsequent
    # phase-runner invocations (Iso639/WordNet/UD/Wiktionary/Tatoeba)
    # see the dependency as satisfied via SequentialPhaseRunner.HydrateStatusAsync
    # and DON'T re-run the legacy C# UcdUcaDecomposer (which would conflict
    # on substrate.codepoint_property's primary key).
    Invoke-HartStep -Name 'Mark UcdUca completed in monitor.phase_status' -Action {
        $sql = "CALL monitor.update_phase_status('UcdUca', 'completed', NULL)"
        & $psql -h $kv['Host'] -p $kv['Port'] -U $kv['Username'] -d $kv['Database'] -v ON_ERROR_STOP=1 -c $sql
        if ($LASTEXITCODE -ne 0) { throw "monitor.update_phase_status failed (exit $LASTEXITCODE)" }
        Write-HartInfo '  UcdUca = completed (substrate-side)'
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'UcdUca seeded entirely from embedded extension catalog.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
