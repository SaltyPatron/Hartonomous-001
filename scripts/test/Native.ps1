#requires -Version 7
<#
.SYNOPSIS
  Run the native Google Test suite (ctest) for libhartonomous.

.PARAMETER Configuration
  Must match the configuration used at build time.

.PARAMETER Rebuild
  Run the full configure + build first.

.EXAMPLE
  pwsh scripts/test/Native.ps1
  pwsh scripts/test/Native.ps1 -Configuration Debug -Rebuild
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')] [string]$Configuration,
    [switch]$Rebuild
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $Configuration) { $Configuration = $Cfg.Native.Configuration }
Start-HartonomousLog -ScriptName "test.Native.$Configuration" -Cfg $Cfg

try {
    if ($Rebuild) {
        Invoke-HartStep -Name "cmake configure ($Configuration)" -Action {
            Invoke-HartCMakeConfigure -Cfg $Cfg -Configuration $Configuration -WithTests
        }
        Invoke-HartStep -Name "cmake build ($Configuration)" -Action {
            Invoke-HartCMakeBuild -Cfg $Cfg -Configuration $Configuration
        }
    }
    Invoke-HartStep -Name "ctest ($Configuration)" -Action {
        Invoke-HartCTest -Cfg $Cfg -Configuration $Configuration
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Native tests passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
