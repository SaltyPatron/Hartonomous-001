# Hartonomous.Docker — Docker Desktop lifecycle + compose helpers.
# Depends on Hartonomous.Common (must be imported first by the caller).

function Test-HartDockerDaemon {
    [CmdletBinding()]
    param()
    $null = & docker info --format '{{.ServerVersion}}' 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Start-HartDockerDesktop {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg
    )
    if (Test-HartDockerDaemon) {
        Write-HartInfo "Docker daemon already running."
        return
    }

    if (-not $IsWindows) {
        throw "Docker daemon not reachable. Auto-start only supported on Windows (Docker Desktop). On Linux/macOS start dockerd manually."
    }

    $candidates = @(foreach ($p in $Cfg.Docker.DesktopCandidatePaths) {
        $expanded = $ExecutionContext.InvokeCommand.ExpandString($p)
        if (Test-Path $expanded) { $expanded }
    })
    if ($candidates.Count -eq 0) {
        throw "Docker Desktop executable not found in any of: $($Cfg.Docker.DesktopCandidatePaths -join ', '). Install Docker Desktop or start it manually."
    }

    Write-HartInfo "Launching Docker Desktop: $($candidates[0])"
    Start-Process -FilePath $candidates[0] -WindowStyle Hidden | Out-Null

    Wait-HartCondition -Label 'Docker daemon' `
        -TimeoutSec $Cfg.Docker.DesktopStartTimeoutSec `
        -Condition { Test-HartDockerDaemon }
    Write-HartInfo "Docker daemon is up."
}

function Invoke-HartCompose {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]]$Argv
    )
    $composePath = Join-Path $Cfg.Repo.Root $Cfg.Docker.ComposeFile
    Assert-HartPath -Path $composePath -Label 'compose file'
    $baseArgs = @('compose', '-p', $Cfg.Docker.ComposeProject, '-f', $composePath)
    Invoke-HartNative -FilePath 'docker' -Argv ($baseArgs + $Argv)
}

function Test-HartContainerExists {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name
    )
    $id = & docker ps -a -q --filter "name=^${Name}$" 2>$null
    return ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($id))
}

function Test-HartContainerRunning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name
    )
    $id = & docker ps -q --filter "name=^${Name}$" 2>$null
    return ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($id))
}

function Get-HartContainerHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name
    )
    if (-not (Test-HartContainerExists -Name $Name)) { return 'absent' }
    $state = & docker inspect --format '{{.State.Health.Status}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return 'unknown' }
    return $state.Trim()
}

function Wait-HartContainerHealthy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [int]$TimeoutSec = 120
    )
    Wait-HartCondition -Label "$Name healthy" -TimeoutSec $TimeoutSec -Condition {
        (Get-HartContainerHealth -Name $Name) -eq 'healthy'
    }
}

function Invoke-HartContainerExec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Container,
        [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]]$Argv,
        [switch]$Interactive
    )
    $base = @('exec')
    if ($Interactive) { $base += '-it' }
    $base += $Container
    Invoke-HartNative -FilePath 'docker' -Argv ($base + $Argv)
}

Export-ModuleMember -Function `
    Test-HartDockerDaemon, Start-HartDockerDesktop, `
    Invoke-HartCompose, `
    Test-HartContainerExists, Test-HartContainerRunning, Get-HartContainerHealth, Wait-HartContainerHealthy, `
    Invoke-HartContainerExec
