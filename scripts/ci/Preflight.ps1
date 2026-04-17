#requires -Version 7
<#
.SYNOPSIS
  Verify every prerequisite is present at the required version before any
  build/seed operation. Exits non-zero on the first missing dependency.

.PARAMETER SkipDocker
  Don't require docker (use inside CI shells that haven't started the service
  yet).

.EXAMPLE
  pwsh scripts/ci/Preflight.ps1
  pwsh scripts/ci/Preflight.ps1 -SkipDocker
#>
[CmdletBinding()]
param(
    [switch]$SkipDocker,
    [switch]$SkipCMake
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'ci.Preflight' -Cfg $Cfg

$problems = @()

function Check {
    param([string]$Label, [scriptblock]$Action)
    try {
        & $Action
        Write-HartInfo "OK — $Label"
    } catch {
        Write-HartError "FAIL — $Label — $($_.Exception.Message)"
        $script:problems += $Label
    }
}

Write-HartBanner 'Preflight checks'

Check 'pwsh ≥ 7' {
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw "pwsh $($PSVersionTable.PSVersion) is too old (need >= 7)." }
}

Check '.NET SDK 9' {
    $info = Resolve-HartDotnet -Cfg $Cfg
    Write-HartDebug "dotnet $($info.Version) at $($info.Path)"
}

if (-not $SkipCMake) {
    Check 'CMake' {
        $info = Resolve-HartCMake -Cfg $Cfg
        Write-HartDebug $info.Version
    }
}

if (-not $SkipDocker) {
    Check 'docker CLI' {
        Assert-HartCommand -Name 'docker' | Out-Null
    }
}

Check 'solution present' {
    Assert-HartPath -Path (Join-Path $Cfg.Repo.Root $Cfg.Paths.Solution) -Label 'solution'
}

Check 'migrations dir' {
    Assert-HartPath -Path (Join-Path $Cfg.Repo.Root $Cfg.Paths.MigrationsDir) -Label 'migrations dir'
}

Check 'native src' {
    Assert-HartPath -Path (Join-Path $Cfg.Repo.Root $Cfg.Paths.LibHartonomousSrc) -Label 'libhartonomous src'
    Assert-HartPath -Path (Join-Path $Cfg.Repo.Root $Cfg.Paths.PgExtensionSrc)    -Label 'hartonomous_pg src'
}

if ($problems.Count -gt 0) {
    Write-HartError ("Preflight failed: $($problems -join ', ')")
    Exit-Hartonomous -Code $Cfg.ExitCodes.Config
}
Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'All preflight checks passed.'
