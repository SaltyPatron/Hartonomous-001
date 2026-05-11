#requires -Version 7
<#
.SYNOPSIS
  Concatenate the canonical extension SQL from sql/schema/**/*.sql + the
  hand-written .sql.in C-binding template into ext/hartonomous_pg/sql/
  hartonomous--1.0.sql.

.DESCRIPTION
  Pre-build step for the PG extension. Walks bootstrap.sql @include
  directives, strips psql meta-commands, inserts the C-binding template
  at the right point in the order, emits the consolidated extension
  script that 'CREATE EXTENSION hartonomous' will run.

  Same mental model as PostGIS / pgvector: maintain many small per-object
  source files; concatenate them in dependency order at build time. The
  generated output is a build artifact (gitignored).

.PARAMETER Check
  Only verify the output exists and is non-empty (CI gate).

.EXAMPLE
  pwsh scripts/build/ExtensionSql.ps1
  pwsh scripts/build/ExtensionSql.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch]$Check
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'build.ExtensionSql' -Cfg $Cfg

$repoRoot  = $Cfg.Repo.Root
$generator = Join-Path $repoRoot 'scripts\build\concat_extension_sql.py'
$bootstrap = Join-Path $repoRoot 'sql\schema\bootstrap.sql'
$template  = Join-Path $repoRoot 'ext\hartonomous_pg\sql\hartonomous--1.0.sql.in'
$output    = Join-Path $repoRoot 'ext\hartonomous_pg\sql\hartonomous--1.0.sql'

try {
    Assert-HartCommand -Name 'python' | Out-Null
    Assert-HartPath -Path $generator -Label 'concat_extension_sql.py'
    Assert-HartPath -Path $bootstrap -Label 'sql/schema/bootstrap.sql'
    Assert-HartPath -Path $template  -Label 'hartonomous--1.0.sql.in (C-binding template)'

    if ($Check) {
        Invoke-HartStep -Name 'Verify hartonomous--1.0.sql' -Action {
            & python $generator --check --output $output
            if ($LASTEXITCODE -ne 0) { throw 'extension SQL missing or too small.' }
        }
    } else {
        Invoke-HartStep -Name 'Concatenate substrate sources → hartonomous--1.0.sql' -Action {
            & python $generator --output $output
            if ($LASTEXITCODE -ne 0) { throw "concat_extension_sql.py failed (exit $LASTEXITCODE)." }
            $sz = (Get-Item $output).Length
            Write-HartInfo ("  output: {0,12:N0} bytes  →  {1}" -f $sz, $output)
        }
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Extension SQL assembly complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
