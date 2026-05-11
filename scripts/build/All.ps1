#requires -Version 7
<#
.SYNOPSIS
  Build every compilable artifact: native → dotnet → PG extension (if container up).

.DESCRIPTION
  Default ordering mirrors CI:
    1. Native libhartonomous (required for P/Invoke tests at dotnet build time).
    2. Managed .NET solution.
    3. PG extension (inside the running container — skipped if container is down).

.PARAMETER DotnetConfiguration
  Debug | Release. Default from config.psd1.

.PARAMETER NativeConfiguration
  Release | Debug | RelWithDebInfo. Default from config.psd1.

.PARAMETER SkipNative
  Don't build the native library.

.PARAMETER SkipDotnet
  Don't build the managed solution.

.PARAMETER SkipPgExtension
  Don't build the PG extension (default behaviour when the container isn't
  running — flip this on if you want to hard-fail on a down container).

.PARAMETER Clean
  Pass -Clean to each sub-script (force full rebuild).

.EXAMPLE
  pwsh scripts/build/All.ps1
  pwsh scripts/build/All.ps1 -DotnetConfiguration Release -NativeConfiguration Release -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$DotnetConfiguration,
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$NativeConfiguration,
    [switch]$SkipNative,
    [switch]$SkipDotnet,
    [switch]$SkipPgExtension,
    [switch]$SkipUnicodeTables,
    [switch]$ForceUnicodeTables,
    [string]$UcdRoot = 'D:\Models\UCD\Public\UCD\latest',
    [switch]$Clean
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'build.All' -Cfg $Cfg

$dotnetCfg = if ($DotnetConfiguration) { $DotnetConfiguration } else { $Cfg.Dotnet.Configuration }
$nativeCfg = if ($NativeConfiguration) { $NativeConfiguration } else { $Cfg.Native.Configuration }

function Invoke-Sub {
    param([string]$Script, [string[]]$Argv)
    $path = Join-Path $PSScriptRoot $Script
    $cmd = @('-File', $path) + $Argv
    pwsh @cmd
    if ($LASTEXITCODE -ne 0) { throw "$Script failed (exit $LASTEXITCODE)." }
}

try {
    if (-not $SkipNative) {
        $argv = @('-Configuration', $nativeCfg)
        if ($Clean) { $argv += '-Clean' }
        Invoke-HartStep -Name "Native ($nativeCfg)" -Action { Invoke-Sub 'Native.ps1' $argv }
    }

    # Generate the embedded UCD/UCA tables BEFORE the PG extension build —
    # the extension's pg_unicode_props.c / pg_codepoint_atoms.c are inputs
    # to its compile. Idempotent: skipped when the generated headers are
    # newer than every UCD source file.
    if (-not $SkipUnicodeTables) {
        $argv = @('-UcdRoot', $UcdRoot)
        if ($ForceUnicodeTables) { $argv += '-Force' }
        Invoke-HartStep -Name 'Unicode tables (UCD/UCA → embedded C arrays)' -Action {
            Invoke-Sub 'UnicodeTables.ps1' $argv
        }
    }

    # Concatenate the canonical extension SQL from sql/schema/* + the
    # hand-written .sql.in C-binding template. PostGIS-style build pattern:
    # multi-file source → single hartonomous--1.0.sql consumed by
    # CREATE EXTENSION hartonomous. Must run BEFORE PgExtension.ps1 so the
    # output is staged into PG's $share/extension/.
    Invoke-HartStep -Name 'Concatenate extension SQL (sql/schema/* → hartonomous--1.0.sql)' -Action {
        Invoke-Sub 'ExtensionSql.ps1' @()
    }

    if (-not $SkipDotnet) {
        $argv = @('-Configuration', $dotnetCfg)
        Invoke-HartStep -Name "Managed ($dotnetCfg)" -Action { Invoke-Sub 'Dotnet.ps1' $argv }
    }

    if (-not $SkipPgExtension) {
        if (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer) {
            # Hot-reload path: container is up, rebuild the extension
            # in-place against /opt/pg18 inside it. Used during dev
            # iteration when you don't want to rebuild the whole image.
            # Force -Target Docker — the running PG is in the container,
            # NOT a host install. Without this, PgExtension.ps1's Auto
            # default picks Windows on Windows hosts and tries to elevate
            # to stage DLLs into C:\Program Files\PostgreSQL\18\ which
            # (a) we don't use and (b) requires admin every run.
            $argv = @('-Target', 'Docker')
            if ($Clean) { $argv += '-Clean' }
            Invoke-HartStep -Name 'PG extension (hot-reload into running container)' -Action { Invoke-Sub 'PgExtension.ps1' $argv }
        } else {
            # Cold-boot path: container is down. The extension will be
            # compiled and installed by docker/pgext.Dockerfile during
            # the next scripts/Docker/Build.ps1 step — that COPYs the
            # freshly-concatenated hartonomous--1.0.sql + ext/hartonomous_pg
            # sources into a builder stage and runs `make install` against
            # the layer-1 postgres headers. No skip; the extension build
            # has just moved out of build.All into Docker.Build.
            Write-HartInfo "PG extension: deferred to docker/pgext.Dockerfile (container is down — cold-boot path)."
        }
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Build.All complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
