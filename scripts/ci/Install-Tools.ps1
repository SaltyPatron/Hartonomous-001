#requires -Version 7
<#
.SYNOPSIS
  Best-effort install of missing prerequisites via winget (Windows only).

.DESCRIPTION
  Only attempts to install what isn't already on PATH. Installs:
    - .NET 9 SDK
    - CMake
    - Docker Desktop
    - Visual Studio 2022 Build Tools (C++ workload)  [fallback when 2026 isn't available]

  On non-Windows hosts this script just prints the equivalent apt/brew commands
  to install manually — we don't sudo.

.EXAMPLE
  pwsh scripts/ci/Install-Tools.ps1
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'ci.Install-Tools' -Cfg $Cfg

function Install-IfMissing {
    param([string]$Command, [string]$WingetId, [string]$Linux, [string]$Mac)
    if (Get-Command $Command -ErrorAction SilentlyContinue) {
        Write-HartInfo "present: $Command"
        return
    }
    if ($IsWindows) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-HartWarn "winget not available — install $Command manually ($WingetId)."
            return
        }
        if ($DryRun) { Write-HartInfo "would winget install --id $WingetId"; return }
        Invoke-HartNative -FilePath 'winget' -Argv @('install','--id',$WingetId,'-e','--accept-source-agreements','--accept-package-agreements') -IgnoreExitCode
    }
    elseif ($IsLinux) {
        Write-HartWarn "Install $Command manually: $Linux"
    }
    elseif ($IsMacOS) {
        Write-HartWarn "Install $Command manually: $Mac"
    }
}

try {
    Install-IfMissing -Command 'dotnet' -WingetId 'Microsoft.DotNet.SDK.9' -Linux 'sudo apt install dotnet-sdk-9.0' -Mac 'brew install dotnet@9'
    Install-IfMissing -Command 'cmake'  -WingetId 'Kitware.CMake'           -Linux 'sudo apt install cmake'         -Mac 'brew install cmake'
    Install-IfMissing -Command 'docker' -WingetId 'Docker.DockerDesktop'    -Linux 'sudo apt install docker.io'     -Mac 'brew install --cask docker'

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Install-Tools finished (re-run Preflight to verify versions).'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
