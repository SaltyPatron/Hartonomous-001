#requires -Version 7
<#
.SYNOPSIS
  Build the hartonomous_pg PostgreSQL extension on either Docker or a native
  Windows Postgres install.

.DESCRIPTION
  Cross-platform build dispatcher. Modeled on pgvector's POSIX Makefile +
  Makefile.win pattern.

  Targets:
    Docker  — `make && make install` inside the running Postgres container.
    Windows — Discover PGROOT, Visual Studio dev tools, and Intel oneAPI;
              build via nmake; idempotently stage extension + Intel runtime
              DLLs into the PG install (auto-elevates only the copy step).

.PARAMETER Target
  Docker | Windows. Default is Windows on Windows hosts, Docker elsewhere.

.PARAMETER PgRoot
  (Windows) Override PGROOT. Falls back to env, then config candidates.

.PARAMETER LibDir
  (Windows) libhartonomous import-library dir. Default
  ext\libhartonomous\build\lib\Release.

.PARAMETER DllDir
  (Windows) libhartonomous DLL dir. Default ext\libhartonomous\build\bin\Release.

.PARAMETER IntelRoot
  (Windows) Intel oneAPI root. Default config WindowsNative.IntelOneApiRoot.

.PARAMETER SkipRuntimeStaging
  (Windows) Skip copying Intel MKL/OpenMP runtime DLLs into PG bin\.

.PARAMETER Clean
  Run `make clean` (or `nmake clean`) before building.

.PARAMETER InstallCheck
  After install, run installcheck (pg_regress).

.EXAMPLE
  pwsh scripts/build/PgExtension.ps1
  pwsh scripts/build/PgExtension.ps1 -Target Docker -InstallCheck
  pwsh scripts/build/PgExtension.ps1 -Clean -InstallCheck
#>
[CmdletBinding()]
param(
    [ValidateSet('Docker', 'Windows', 'Auto')]
    [string]$Target = 'Auto',

    [string]$PgRoot,
    [string]$LibDir,
    [string]$DllDir,
    [string]$IntelRoot,
    [switch]$SkipRuntimeStaging,
    [switch]$Clean,
    [switch]$InstallCheck,

    # When set, regenerate UCD/UCA tables before building. Default: skip (the
    # generated headers in ext/hartonomous_pg/src/generated/ are checked in
    # and only rebuilt when UCD version changes — this can take ~minutes for
    # the 1,114,112-codepoint hash + centroid + Hilbert pre-computation).
    [switch]$RegenerateUnicode,
    [string]$UcdRoot = 'D:\Models\UCD\Public\UCD\latest'
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1"  -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Windows.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'build.PgExtension' -Cfg $Cfg

if ($Target -eq 'Auto') {
    $Target = if ($IsWindows) { 'Windows' } else { 'Docker' }
}

function Invoke-DockerBuild {
    Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force
    Assert-HartCommand -Name 'docker' | Out-Null
    if ($InstallCheck) {
        throw "PgExtension.ps1 -Target Docker -InstallCheck is not supported by the runtime image. Rebuild with -Target Docker, then run scripts/test/PgRegress.ps1 against the recreated container."
    }
    if ($Clean) {
        Write-HartWarn "Docker target rebuilds immutable image layers; -Clean is ignored. Use scripts/docker/Build.ps1 -NoCache when intentionally busting Docker cache."
    }

    $dockerBuild = Join-Path $Cfg.Repo.Root 'scripts\docker\Build.ps1'
    Assert-HartPath -Path $dockerBuild -Label 'Docker image build script'

    Invoke-HartStep -Name 'Build Docker pgext layer from source' -Action {
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $dockerBuild -Layer pgext
        if ($LASTEXITCODE -ne 0) {
            throw "Docker pgext layer build failed (exit $LASTEXITCODE)."
        }
    }

    Invoke-HartStep -Name 'Build Docker final image from source' -Action {
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $dockerBuild -Layer final
        if ($LASTEXITCODE -ne 0) {
            throw "Docker final layer build failed (exit $LASTEXITCODE)."
        }
    }

    Invoke-HartStep -Name "Recreate $($Cfg.Docker.PgContainer) from rebuilt image" -Action {
        Invoke-HartCompose -Cfg $Cfg -Argv @('up', '-d', '--force-recreate', 'postgres')
    }

    Invoke-HartStep -Name "Wait for $($Cfg.Docker.PgContainer) to be healthy" -Action {
        Wait-HartContainerHealthy -Name $Cfg.Docker.PgContainer -TimeoutSec $Cfg.Docker.HealthCheckTimeoutSec
    }
}

function Invoke-WindowsBuild {
    $resolvedPgRoot = Find-HartPgRoot -Cfg $Cfg -Override $PgRoot
    $resolvedIntel  = Find-HartIntelOneApi -Cfg $Cfg -Override $IntelRoot
    $vcvars         = Find-HartVsDevCmd -Cfg $Cfg

    $repoRoot = $Cfg.Repo.Root
    $resolvedLibDir = if ($LibDir) { $LibDir } else { Join-Path $repoRoot 'ext\libhartonomous\build\lib\Release' }
    $resolvedDllDir = if ($DllDir) { $DllDir } else { Join-Path $repoRoot 'ext\libhartonomous\build\bin\Release' }
    Assert-HartPath -Path (Join-Path $resolvedLibDir 'hartonomous.lib') -Label 'hartonomous.lib (run scripts/build/Native.ps1 first)'
    Assert-HartPath -Path (Join-Path $resolvedDllDir 'hartonomous.dll') -Label 'hartonomous.dll (run scripts/build/Native.ps1 first)'

    $extDir = Join-Path $repoRoot 'ext\hartonomous_pg'
    Assert-HartPath -Path $extDir -Label 'hartonomous_pg source dir'

    $cleanLine = if ($Clean) { 'nmake /F Makefile.win clean' + [Environment]::NewLine + 'if errorlevel 1 exit /b 1' } else { 'rem no clean' }

    Invoke-HartStep -Name "nmake build (PG=$resolvedPgRoot)" -Action {
        $bat = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "hart-pgbuild-$([guid]::NewGuid().Guid).bat")
        $vcvarsLine = if ($vcvars) { "call `"$vcvars`" >nul`r`nif errorlevel 1 exit /b 1" } else { 'rem vcvars not required (cl/nmake already on PATH)' }
        $lines = @(
            '@echo off',
            $vcvarsLine,
            "set `"PGROOT=$resolvedPgRoot`"",
            "set `"HARTONOMOUS_LIB_DIR=$resolvedLibDir`"",
            "set `"HARTONOMOUS_DLL_DIR=$resolvedDllDir`"",
            "cd /d `"$extDir`"",
            $cleanLine,
            'nmake /F Makefile.win',
            'exit /b %ERRORLEVEL%'
        )
        Set-Content -LiteralPath $bat -Value ($lines -join [Environment]::NewLine) -Encoding ascii
        try {
            $out = & cmd /c $bat 2>&1
            $out | ForEach-Object { Write-HartTrace ([string]$_) }
            if ($LASTEXITCODE -ne 0) {
                $out | Select-Object -Last 30 | ForEach-Object { Write-HartError ([string]$_) }
                throw "nmake build failed (exit $LASTEXITCODE)."
            }
        }
        finally { Remove-Item -LiteralPath $bat -ErrorAction SilentlyContinue }
    }

    Invoke-HartStep -Name 'Stage extension files into PG install' -Action {
        $extDll  = Join-Path $extDir 'hartonomous.dll'
        $extCtl  = Join-Path $extDir 'hartonomous.control'
        $extSql  = Join-Path $extDir 'sql\hartonomous--1.0.sql'
        Assert-HartPath -Path $extDll -Label 'built extension DLL'
        Assert-HartPath -Path $extCtl -Label 'extension control file'
        Assert-HartPath -Path $extSql -Label 'extension SQL script'

        $pgLib = Join-Path $resolvedPgRoot 'lib'
        $pgExt = Join-Path $resolvedPgRoot 'share\extension'

        Sync-HartFileElevated -Sources @($extDll) -DestinationDir $pgLib -Description 'stage hartonomous.dll' | Out-Null
        Sync-HartFileElevated -Sources @($extCtl, $extSql) -DestinationDir $pgExt -Description 'stage hartonomous.control + SQL script' | Out-Null
    }

    Invoke-HartStep -Name 'Stage UCD atoms blob into PG share\extension\hartonomous-ucd' -Action {
        # Per-block math files + index + global reverse table. Backend
        # _PG_init() mmaps these on startup; without them, cp_hash() etc.
        # return NULL.
        $genDir   = Join-Path $repoRoot 'ext\hartonomous_pg\src\generated'
        $idx      = Join-Path $genDir 'hartonomous-ucd-17.0.0.idx'
        $reverse  = Join-Path $genDir 'hartonomous-ucd-17.0.0.reverse.bin'
        $blocks   = Join-Path $genDir 'blocks'
        Assert-HartPath -Path $idx     -Label 'UCD atoms index (run UnicodeTables.ps1 first)'
        Assert-HartPath -Path $reverse -Label 'UCD atoms reverse table'
        Assert-HartPath -Path $blocks  -Label 'UCD atoms blocks dir'

        $blobBase   = Join-Path $resolvedPgRoot 'share\extension\hartonomous-ucd'
        $blobBlocks = Join-Path $blobBase 'blocks'
        Sync-HartFileElevated -Sources @($idx, $reverse) -DestinationDir $blobBase -Description 'stage UCD atoms idx + reverse' | Out-Null
        $blockFiles = Get-ChildItem -LiteralPath $blocks -Filter '*.bin' | ForEach-Object { $_.FullName }
        if ($blockFiles.Count -lt 100) {
            throw "expected ~397 block files under $blocks; found $($blockFiles.Count)"
        }
        Sync-HartFileElevated -Sources $blockFiles -DestinationDir $blobBlocks -Description "stage $($blockFiles.Count) UCD block files" | Out-Null
    }

    if (-not $SkipRuntimeStaging) {
        Invoke-HartStep -Name 'Stage Intel oneAPI runtime DLLs into PG bin\' -Action {
            $libDll = Join-Path $resolvedDllDir 'hartonomous.dll'
            $intelDlls = Get-HartIntelRuntimeFiles -Cfg $Cfg -IntelRoot $resolvedIntel
            $pgBin = Join-Path $resolvedPgRoot 'bin'
            $sources = @($libDll) + $intelDlls
            $changed = Sync-HartFileElevated -Sources $sources -DestinationDir $pgBin -Description "stage libhartonomous + $($intelDlls.Count) Intel runtime DLLs"
            if ($changed -gt 0) {
                Write-HartWarn 'PG bin\ changed; restart postgresql-x64-* service if it is currently running so it picks up the new DLLs.'
            }
        }
    }

    if ($InstallCheck) {
        Invoke-HartStep -Name 'nmake installcheck' -Action {
            $bat = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "hart-pgcheck-$([guid]::NewGuid().Guid).bat")
            $vcvarsLine = if ($vcvars) { "call `"$vcvars`" >nul`r`nif errorlevel 1 exit /b 1" } else { 'rem vcvars not required' }
            $lines = @(
                '@echo off',
                $vcvarsLine,
                "set `"PGROOT=$resolvedPgRoot`"",
                "cd /d `"$extDir`"",
                'nmake /F Makefile.win installcheck',
                'exit /b %ERRORLEVEL%'
            )
            Set-Content -LiteralPath $bat -Value ($lines -join [Environment]::NewLine) -Encoding ascii
            try {
                & cmd /c $bat
                if ($LASTEXITCODE -ne 0) { throw "pg_regress reported failures: $LASTEXITCODE" }
            }
            finally { Remove-Item -LiteralPath $bat -ErrorAction SilentlyContinue }
        }
    }
}

try {
    if ($RegenerateUnicode) {
        Invoke-HartStep -Name "Regenerate UCD/UCA tables (UCD root: $UcdRoot)" -Action {
            $gen = Join-Path $Cfg.Repo.Root 'scripts\build\generate_unicode_tables.py'
            Assert-HartPath -Path $gen -Label 'generate_unicode_tables.py'
            $outDir = Join-Path $Cfg.Repo.Root 'ext\hartonomous_pg\src\generated'
            & python $gen --ucd-root $UcdRoot --out $outDir
            if ($LASTEXITCODE -ne 0) { throw "Unicode table generator failed (exit $LASTEXITCODE)." }
        }
    }

    switch ($Target) {
        'Docker'  { Invoke-DockerBuild }
        'Windows' { Invoke-WindowsBuild }
        default   { throw "Unknown Target '$Target'." }
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "PG extension built and installed via $Target target."
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
