#requires -Version 7
<#
.SYNOPSIS
  Run the .NET test suite with TRX + coverage output to reports/.

.PARAMETER Configuration
  Debug | Release.

.PARAMETER Filter
  xUnit/.NET filter expression passed via --filter.

.PARAMETER Project
  Run a specific test project; default = whole solution.

.PARAMETER NoBuild
  Don't rebuild before testing.

.PARAMETER NoCoverage
  Skip `--collect "XPlat Code Coverage"`.

.EXAMPLE
  pwsh scripts/test/Dotnet.ps1
  pwsh scripts/test/Dotnet.ps1 -Filter 'FullyQualifiedName~Hartonomous.Core.Tests.Text'
  pwsh scripts/test/Dotnet.ps1 -Project tests/Hartonomous.Engine.Tests/Hartonomous.Engine.Tests.csproj
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$Configuration,
    [string]$Filter,
    [string]$Project,
    [switch]$NoBuild,
    [switch]$NoCoverage
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force

$Cfg = Get-HartonomousConfig
if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
Start-HartonomousLog -ScriptName "test.Dotnet.$Configuration" -Cfg $Cfg

try {
    Invoke-HartStep -Name "dotnet test ($Configuration)" -Action {
        Invoke-HartDotnetTest -Cfg $Cfg `
                              -Configuration $Configuration `
                              -Filter $Filter `
                              -Project $Project `
                              -NoBuild:$NoBuild `
                              -Coverage:(-not $NoCoverage)
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "Tests passed. Reports → $($Cfg.Paths.Reports)"
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
