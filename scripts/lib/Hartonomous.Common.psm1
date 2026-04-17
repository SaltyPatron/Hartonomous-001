# Hartonomous.Common — shared foundation for ops scripts.
# Imports the data-only config, provides logging, step orchestration, preflight
# assertions, and a handful of polling/process helpers used by every script.
#
# Every script should:
#   Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
#   $Cfg = Get-HartonomousConfig
#   Invoke-HartStep -Name 'Do a thing' -Action { ... } -Cfg $Cfg

$script:LogFile = $null
$script:LogContext = $null

enum HartLogLevel { Trace = 0; Debug = 1; Info = 2; Warn = 3; Error = 4 }

function Get-HartonomousRepoRoot {
    [CmdletBinding()]
    param()
    # Walk up from this module until we find the .slnx file.
    $dir = Split-Path -Parent $PSScriptRoot
    while ($null -ne $dir) {
        if (Test-Path (Join-Path $dir 'Hartonomous.slnx')) { return (Resolve-Path $dir).Path }
        $parent = Split-Path -Parent $dir
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    throw "Could not locate repo root (no Hartonomous.slnx up the tree from $PSScriptRoot)."
}

function Get-HartonomousConfig {
    [CmdletBinding()]
    param(
        [string]$ConfigPath
    )
    $repoRoot = Get-HartonomousRepoRoot
    if (-not $ConfigPath) {
        $ConfigPath = Join-Path $repoRoot 'scripts\config.psd1'
    }
    if (-not (Test-Path $ConfigPath)) {
        throw "Config not found: $ConfigPath"
    }
    $cfg = Import-PowerShellDataFile -Path $ConfigPath
    $cfg.Repo.Root = $repoRoot

    # ── Env-var overlay ────────────────────────────────────────────────────
    # HARTONOMOUS_DB short-circuits Postgres.ConnectionString.
    if ($env:HARTONOMOUS_DB) { $cfg.Postgres.ConnectionString = $env:HARTONOMOUS_DB }

    # Individual Postgres overrides.
    if ($env:HARTONOMOUS_POSTGRES__HOST)     { $cfg.Postgres.Host     = $env:HARTONOMOUS_POSTGRES__HOST }
    if ($env:HARTONOMOUS_POSTGRES__PORT)     { $cfg.Postgres.Port     = [int]$env:HARTONOMOUS_POSTGRES__PORT }
    if ($env:HARTONOMOUS_POSTGRES__USER)     { $cfg.Postgres.User     = $env:HARTONOMOUS_POSTGRES__USER }
    if ($env:HARTONOMOUS_POSTGRES__PASSWORD) { $cfg.Postgres.Password = $env:HARTONOMOUS_POSTGRES__PASSWORD }
    if ($env:HARTONOMOUS_POSTGRES__DATABASE) { $cfg.Postgres.Database = $env:HARTONOMOUS_POSTGRES__DATABASE }

    if ($env:HARTONOMOUS_PATHS__SOURCEROOT)  { $cfg.Paths.SourceRoot  = $env:HARTONOMOUS_PATHS__SOURCEROOT }

    if ($env:HARTONOMOUS_DOTNET__CONFIGURATION) { $cfg.Dotnet.Configuration = $env:HARTONOMOUS_DOTNET__CONFIGURATION }
    if ($env:HARTONOMOUS_NATIVE__CONFIGURATION) { $cfg.Native.Configuration = $env:HARTONOMOUS_NATIVE__CONFIGURATION }

    # Build the connection string if not explicitly provided.
    if (-not $cfg.Postgres.ConnectionString) {
        $cfg.Postgres.ConnectionString = "Host=$($cfg.Postgres.Host);Port=$($cfg.Postgres.Port);Username=$($cfg.Postgres.User);Password=$($cfg.Postgres.Password);Database=$($cfg.Postgres.Database)"
    }

    return $cfg
}

function Start-HartonomousLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ScriptName,
        [Parameter(Mandatory)] $Cfg
    )
    $script:LogContext = $ScriptName
    $logsDir = Join-Path $Cfg.Repo.Root $Cfg.Paths.Logs
    if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }
    $date = Get-Date -Format 'yyyyMMdd'
    $name = $Cfg.Logging.FileNameFormat `
        -replace '\{Script\}', $ScriptName `
        -replace '\{Date\}',   $date `
        -replace '\{Pid\}',    $PID
    $script:LogFile = Join-Path $logsDir $name
    "[$(Get-Date -Format 'u')] BEGIN $ScriptName (pid=$PID)" | Out-File -Append -FilePath $script:LogFile -Encoding utf8
}

function Stop-HartonomousLog {
    [CmdletBinding()]
    param([int]$ExitCode = 0)
    if ($script:LogFile) {
        "[$(Get-Date -Format 'u')] END   $script:LogContext exit=$ExitCode" |
            Out-File -Append -FilePath $script:LogFile -Encoding utf8
    }
    $script:LogFile = $null
    $script:LogContext = $null
}

function Write-HartLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [HartLogLevel]$Level,
        [Parameter(Mandatory, ValueFromPipeline)] [string]$Message
    )
    $ts = Get-Date -Format 'HH:mm:ss.fff'
    $ctx = $script:LogContext ? $script:LogContext : 'hart'
    $line = "[$ts] [$Level] [$ctx] $Message"

    $color = switch ($Level) {
        ([HartLogLevel]::Trace) { 'DarkGray' }
        ([HartLogLevel]::Debug) { 'Gray' }
        ([HartLogLevel]::Info)  { 'White' }
        ([HartLogLevel]::Warn)  { 'Yellow' }
        ([HartLogLevel]::Error) { 'Red' }
    }
    Write-Host $line -ForegroundColor $color

    if ($script:LogFile) {
        "[$(Get-Date -Format 'u')] [$Level] $Message" |
            Out-File -Append -FilePath $script:LogFile -Encoding utf8
    }
}

function Write-HartTrace { param([string]$Message) Write-HartLog -Level Trace -Message $Message }
function Write-HartDebug { param([string]$Message) Write-HartLog -Level Debug -Message $Message }
function Write-HartInfo  { param([string]$Message) Write-HartLog -Level Info  -Message $Message }
function Write-HartWarn  { param([string]$Message) Write-HartLog -Level Warn  -Message $Message }
function Write-HartError { param([string]$Message) Write-HartLog -Level Error -Message $Message }

function Write-HartBanner {
    param([string]$Title)
    $bar = '=' * 78
    Write-Host ''
    Write-Host $bar -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host $bar -ForegroundColor Cyan
}

function Invoke-HartStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [scriptblock]$Action,
        [switch]$ContinueOnError
    )
    Write-HartBanner $Name
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $sw.Stop()
        Write-HartInfo ("{0} — OK ({1:N1}s)" -f $Name, $sw.Elapsed.TotalSeconds)
    }
    catch {
        $sw.Stop()
        Write-HartError ("{0} — FAILED after {1:N1}s: {2}" -f $Name, $sw.Elapsed.TotalSeconds, $_.Exception.Message)
        if (-not $ContinueOnError) { throw }
    }
}

function Assert-HartCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [string]$InstallHint
    )
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) {
        $hint = $InstallHint ? " ($InstallHint)" : ''
        throw "Required command not found on PATH: $Name$hint"
    }
    return $cmd
}

function Assert-HartPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Label
    )
    if (-not (Test-Path $Path)) {
        throw "Missing $Label`: $Path"
    }
}

function Assert-HartMinVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Actual,
        [Parameter(Mandatory)] [string]$Minimum,
        [Parameter(Mandatory)] [string]$Label
    )
    # Extract leading x.y.z from the actual string (dotnet returns "9.0.100-xyz").
    if ($Actual -notmatch '(\d+\.\d+(\.\d+)?)') {
        throw "$Label version string not parseable: $Actual"
    }
    $ver = [version]$Matches[1]
    $min = [version]$Minimum
    if ($ver -lt $min) {
        throw "$Label version $ver is below minimum $min."
    }
}

function Wait-HartCondition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock]$Condition,
        [Parameter(Mandatory)] [string]$Label,
        [int]$TimeoutSec = 60,
        [int]$PollSec = 2
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            if (& $Condition) { return }
        }
        catch {
            Write-HartTrace "Wait-HartCondition: $Label condition threw: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds $PollSec
    }
    throw "Timed out after ${TimeoutSec}s waiting for: $Label"
}

function Invoke-HartNative {
    # Runs an external command, streams output, asserts exit code.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(ValueFromRemainingArguments)] [string[]]$Argv,
        [string]$WorkingDirectory,
        [int]$ExpectedExitCode = 0,
        [switch]$IgnoreExitCode
    )
    $cwd = if ($WorkingDirectory) { Resolve-Path $WorkingDirectory } else { (Get-Location).Path }
    Write-HartDebug ("exec: {0} {1}  (cwd={2})" -f $FilePath, ($Argv -join ' '), $cwd)
    $prevCwd = Get-Location
    try {
        if ($WorkingDirectory) { Set-Location $cwd }
        & $FilePath @Argv
        $code = $LASTEXITCODE
    }
    finally {
        if ($WorkingDirectory) { Set-Location $prevCwd }
    }
    if (-not $IgnoreExitCode -and $code -ne $ExpectedExitCode) {
        throw "Command failed: $FilePath $($Argv -join ' ') (exit $code)"
    }
    return $code
}

function Exit-Hartonomous {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [int]$Code,
        [string]$Message
    )
    if ($Message) {
        if ($Code -eq 0) { Write-HartInfo $Message } else { Write-HartError $Message }
    }
    Stop-HartonomousLog -ExitCode $Code
    exit $Code
}

Export-ModuleMember -Function `
    Get-HartonomousRepoRoot, Get-HartonomousConfig, `
    Start-HartonomousLog, Stop-HartonomousLog, `
    Write-HartLog, Write-HartTrace, Write-HartDebug, Write-HartInfo, Write-HartWarn, Write-HartError, Write-HartBanner, `
    Invoke-HartStep, Invoke-HartNative, `
    Assert-HartCommand, Assert-HartPath, Assert-HartMinVersion, `
    Wait-HartCondition, Exit-Hartonomous
