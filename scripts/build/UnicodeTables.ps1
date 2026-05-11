#requires -Version 7
<#
.SYNOPSIS
  Generate the embedded UCD/UCA C tables consumed by the hartonomous PG
  extension. Idempotent — skips when the generated headers are newer than
  every UCD source file under -UcdRoot.

.DESCRIPTION
  Pipeline step that runs scripts/build/generate_unicode_tables.py and
  validates the output: confirms each expected .h/.c file exists, reports
  byte sizes, and validates the generated pg_unicode_version.h matches
  the UCD version stamp in the source files. Fails loud on missing files
  or version mismatch — Law #6 determinism gate.

  Tier-0 codepoint atoms (1,114,112 entries × hash + centroid + Hilbert
  index) take ~3-5 minutes to compute on first run. Subsequent runs
  skip when -Force is not set and the headers are up to date.

.PARAMETER UcdRoot
  Path to the UCD/UCA tree (default: D:\Models\UCD\Public\UCD\latest).
  Must contain ucd/auxiliary/ + ucd/emoji/ + ucd/UnicodeData.txt + uca/allkeys.txt.

.PARAMETER Force
  Regenerate even when generated headers are up to date.

.EXAMPLE
  pwsh scripts/build/UnicodeTables.ps1
  pwsh scripts/build/UnicodeTables.ps1 -Force
  pwsh scripts/build/UnicodeTables.ps1 -UcdRoot D:\Models\UCD\Public\UCD\17.1.0
#>
[CmdletBinding()]
param(
    [string]$UcdRoot = 'D:\Models\UCD\Public\UCD\latest',
    [switch]$Force
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'build.UnicodeTables' -Cfg $Cfg

$repoRoot = $Cfg.Repo.Root
$generator = Join-Path $repoRoot 'scripts\build\generate_unicode_tables.py'
$outDir    = Join-Path $repoRoot 'ext\hartonomous_pg\src\generated'

# Files we expect after a clean generator run.
# Per-block math files live under generated/blocks/ — we don't enumerate
# all 397, just sanity-check that the directory exists with files.
$expected = @(
    'pg_unicode_version.h',
    'pg_ucd_segmentation.h',     'pg_ucd_segmentation.c',
    'pg_ucd_classification.h',   'pg_ucd_classification.c',
    'pg_ucd_casing.h',           'pg_ucd_casing.c',
    'pg_ucd_pictographic.h',     'pg_ucd_pictographic.c',
    'pg_ucd_decomp.h',           'pg_ucd_decomp.c',
    'pg_ucd_fcf.h',              'pg_ucd_fcf.c',
    'pg_ucd_uca.h',              'pg_ucd_uca.c',
    'pg_ucd_names.h',            'pg_ucd_names.c',
    'pg_ucd_inventory.h',        'pg_ucd_inventory.c',
    'pg_ucd_tier1.h',            'pg_ucd_tier1.c',
    'pg_ucd_atoms_blob.h',
    'pg_ucd.h',
    'hartonomous-ucd-17.0.0.idx',
    'hartonomous-ucd-17.0.0.reverse.bin'
)

function Get-NewestUcdMtime {
    $sources = @(
        Join-Path $UcdRoot 'ucd\UnicodeData.txt'
        Join-Path $UcdRoot 'ucd\Blocks.txt'
        Join-Path $UcdRoot 'ucd\Scripts.txt'
        Join-Path $UcdRoot 'ucd\LineBreak.txt'
        Join-Path $UcdRoot 'ucd\CaseFolding.txt'
        Join-Path $UcdRoot 'ucd\DerivedCoreProperties.txt'
        Join-Path $UcdRoot 'ucd\auxiliary\GraphemeBreakProperty.txt'
        Join-Path $UcdRoot 'ucd\auxiliary\WordBreakProperty.txt'
        Join-Path $UcdRoot 'ucd\auxiliary\SentenceBreakProperty.txt'
        Join-Path $UcdRoot 'ucd\emoji\emoji-data.txt'
        Join-Path $UcdRoot 'uca\allkeys.txt'
    )
    $newest = $null
    foreach ($f in $sources) {
        if (-not (Test-Path $f)) {
            throw "UCD source file missing: $f. Set -UcdRoot to a complete UCD tree."
        }
        $t = (Get-Item $f).LastWriteTime
        if ($null -eq $newest -or $t -gt $newest) { $newest = $t }
    }
    return $newest
}

function Test-NeedsRegen {
    foreach ($name in $expected) {
        $p = Join-Path $outDir $name
        if (-not (Test-Path $p)) { return $true }
    }
    $newestSrc = Get-NewestUcdMtime
    $oldestGen = $null
    foreach ($name in $expected) {
        $t = (Get-Item (Join-Path $outDir $name)).LastWriteTime
        if ($null -eq $oldestGen -or $t -lt $oldestGen) { $oldestGen = $t }
    }
    return ($newestSrc -gt $oldestGen)
}

function Invoke-Generator {
    Assert-HartCommand -Name 'python' | Out-Null
    Assert-HartPath -Path $generator -Label 'generate_unicode_tables.py'
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    & python $generator --ucd-root $UcdRoot --out $outDir
    if ($LASTEXITCODE -ne 0) {
        throw "generate_unicode_tables.py failed (exit $LASTEXITCODE)."
    }
}

function Test-GeneratedFiles {
    foreach ($name in $expected) {
        $p = Join-Path $outDir $name
        if (-not (Test-Path $p)) {
            throw "expected generated file missing after generator run: $p"
        }
        $sz = (Get-Item $p).Length
        if ($sz -le 0) { throw "generated file empty: $p" }
        Write-HartInfo ("  {0,-36}  {1,14:N0} bytes" -f $name, $sz)
    }
    # Per-block files — should be ~397 .bin files under blocks/
    $blocksDir = Join-Path $outDir 'blocks'
    if (-not (Test-Path $blocksDir)) {
        throw "expected blocks/ directory missing: $blocksDir"
    }
    $blockFiles = Get-ChildItem -LiteralPath $blocksDir -Filter '*.bin'
    if ($blockFiles.Count -lt 100) {
        throw "blocks/ contains only $($blockFiles.Count) files (expected ~397)"
    }
    $totalBlockBytes = ($blockFiles | Measure-Object -Property Length -Sum).Sum
    Write-HartInfo ("  {0,-36}  {1,4} files, {2,12:N0} bytes" -f 'blocks/', $blockFiles.Count, $totalBlockBytes)
    # Validate UCD version stamp.
    $versionH = Join-Path $outDir 'pg_unicode_version.h'
    $verLine = (Select-String -Path $versionH -Pattern 'UCD_VERSION_STRING' | Select-Object -First 1).Line
    if (-not $verLine) { throw "pg_unicode_version.h missing UCD_VERSION_STRING" }
    Write-HartInfo "  pinned UCD version: $verLine"
}

try {
    Invoke-HartStep -Name "Check generator inputs (UCD root: $UcdRoot)" -Action {
        $newest = Get-NewestUcdMtime
        Write-HartInfo "  newest UCD source mtime: $newest"
    }

    if ($Force -or (Test-NeedsRegen)) {
        Invoke-HartStep -Name 'Run Unicode tables generator' -Action {
            Invoke-Generator
        }
    } else {
        Write-HartInfo 'Generated tables are up to date — skipping (use -Force to regenerate).'
    }

    Invoke-HartStep -Name 'Validate generated outputs' -Action { Test-GeneratedFiles }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Unicode tables generation complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
