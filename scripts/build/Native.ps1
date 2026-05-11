#requires -Version 7
<#
.SYNOPSIS
  Configure + build the native libhartonomous library via CMake.

.PARAMETER Configuration
  Release | Debug | RelWithDebInfo | MinSizeRel. Default from config.psd1.

.PARAMETER Clean
  Remove the CMake build dir first (forces a from-scratch configure).

.PARAMETER NoTests
  Skip building the Google Test suite (faster; CI uses this for dotnet lane).

.EXAMPLE
  pwsh scripts/build/Native.ps1
  pwsh scripts/build/Native.ps1 -Configuration Release -Clean
  pwsh scripts/build/Native.ps1 -NoTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release','RelWithDebInfo','MinSizeRel')]
    [string]$Configuration,
    [switch]$Clean,
    [switch]$NoTests
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $Configuration) { $Configuration = $Cfg.Native.Configuration }
Start-HartonomousLog -ScriptName "build.Native.$Configuration" -Cfg $Cfg

try {
    if ($Clean) {
        Invoke-HartStep -Name 'Clean native build dir' -Action { Invoke-HartCMakeClean -Cfg $Cfg }
    }
    Invoke-HartStep -Name "cmake configure ($Configuration)" -Action {
        Invoke-HartCMakeConfigure -Cfg $Cfg -Configuration $Configuration -WithTests:(-not $NoTests)
    }
    Invoke-HartStep -Name "cmake build ($Configuration)" -Action {
        Invoke-HartCMakeBuild -Cfg $Cfg -Configuration $Configuration
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Native build complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
