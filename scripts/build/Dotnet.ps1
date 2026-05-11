#requires -Version 7
<#
.SYNOPSIS
  Build the .NET solution.

.PARAMETER Configuration
  Debug | Release. Default from config.psd1.

.PARAMETER Restore
  Run `dotnet restore` before build (default: $true).

.EXAMPLE
  pwsh scripts/build/Dotnet.ps1
  pwsh scripts/build/Dotnet.ps1 -Configuration Release
  pwsh scripts/build/Dotnet.ps1 -Configuration Release -Restore:$false
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration,
    [bool]$Restore = $true
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
Start-HartonomousLog -ScriptName "build.Dotnet.$Configuration" -Cfg $Cfg

try {
    if ($Restore) {
        Invoke-HartStep -Name 'dotnet restore' -Action { Invoke-HartDotnetRestore -Cfg $Cfg }
    }
    Invoke-HartStep -Name "dotnet build ($Configuration)" -Action {
        Invoke-HartDotnetBuild -Cfg $Cfg -Configuration $Configuration -NoRestore:(-not $Restore)
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Managed build complete.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
