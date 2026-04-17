# Hartonomous.Phases — wraps `dotnet run -- phases ...` invocations.

function Invoke-HartCli {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]]$CliArgs,
        [switch]$NoBuild
    )
    $cliProj = Join-Path $Cfg.Repo.Root 'src\Hartonomous.Cli'
    Assert-HartPath -Path $cliProj -Label 'Hartonomous.Cli project'

    $argv = @('run', '--project', $cliProj, '-c', $Cfg.Dotnet.Configuration)
    if ($NoBuild) { $argv += '--no-build' }
    $argv += '--'
    $argv += $CliArgs

    Invoke-HartNative -FilePath 'dotnet' -Argv $argv -WorkingDirectory $Cfg.Repo.Root
}

function Invoke-HartMigrate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory)] [ValidateSet('up','down','status')] [string]$Action,
        [int]$Target,
        [switch]$NoBuild
    )
    $cliArgs = @('migrate', $Action, '--connection', $Cfg.Postgres.ConnectionString)
    if ($Action -eq 'down' -and $PSBoundParameters.ContainsKey('Target')) {
        $cliArgs += $Target
    }
    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs $cliArgs
}

function Invoke-HartPhase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory)] [string]$Phase,
        [string]$SourceRoot,
        [switch]$SkipDeps,
        [switch]$DryRun,
        [switch]$NoBuild
    )
    if (-not $SourceRoot) { $SourceRoot = $Cfg.Paths.SourceRoot }
    $cliArgs = @('phases', 'run',
                 '--phase',      $Phase,
                 '--connection', $Cfg.Postgres.ConnectionString,
                 '--source',     $SourceRoot)
    if ($SkipDeps) { $cliArgs += '--skip-deps' }
    if ($DryRun)   { $cliArgs += '--dry-run' }
    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs $cliArgs
}

function Get-HartPhaseStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg, [switch]$NoBuild)
    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs @('phases', 'status', '--connection', $Cfg.Postgres.ConnectionString)
}

function Get-HartPhaseList {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Cfg, [switch]$NoBuild)
    Invoke-HartCli -Cfg $Cfg -NoBuild:$NoBuild -CliArgs @('phases', 'list')
}

Export-ModuleMember -Function `
    Invoke-HartCli, Invoke-HartMigrate, Invoke-HartPhase, Get-HartPhaseStatus, Get-HartPhaseList
