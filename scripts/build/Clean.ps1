#requires -Version 7
<#
.SYNOPSIS
  Remove all build artifacts (bin/, obj/, ext/*/build/).

.PARAMETER Managed
  Also run `dotnet clean` for the solution (deep clean; slower).

.EXAMPLE
  pwsh scripts/build/Clean.ps1
  pwsh scripts/build/Clean.ps1 -Managed
#>
[CmdletBinding()]
param(
    [switch]$Managed
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'build.Clean' -Cfg $Cfg

try {
    Invoke-HartStep -Name 'Remove bin/ + obj/ under src and tests' -Action {
        $targets = @('src','tests') | ForEach-Object { Join-Path $Cfg.Repo.Root $_ }
        foreach ($t in $targets) {
            if (Test-Path $t) {
                Get-ChildItem -Path $t -Recurse -Directory -Force |
                    Where-Object { $_.Name -in 'bin','obj' } |
                    ForEach-Object {
                        Write-HartDebug "rm -rf $($_.FullName)"
                        Remove-Item -Recurse -Force $_.FullName
                    }
            }
        }
    }

    Invoke-HartStep -Name 'Remove native build dir' -Action {
        Invoke-HartCMakeClean -Cfg $Cfg
    }

    if ($Managed) {
        Invoke-HartStep -Name 'dotnet clean' -Action {
            Invoke-HartDotnetClean -Cfg $Cfg
        }
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Clean complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
