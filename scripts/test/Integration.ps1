#requires -Version 7
<#
.SYNOPSIS
  Run the Hartonomous.Integration.Tests project (requires Docker).

.EXAMPLE
  pwsh scripts/test/Integration.ps1
  pwsh scripts/test/Integration.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$Configuration
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Build.psm1"  -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Docker.psm1" -Force

$Cfg = Get-HartonomousConfig
if (-not $Configuration) { $Configuration = $Cfg.Dotnet.Configuration }
Start-HartonomousLog -ScriptName "test.Integration.$Configuration" -Cfg $Cfg

try {
    if (-not (Test-HartContainerRunning -Name $Cfg.Docker.PgContainer)) {
        Write-HartError "Integration tests require $($Cfg.Docker.PgContainer) to be running."
        Exit-Hartonomous -Code $Cfg.ExitCodes.Unavailable
    }
    $proj = Join-Path $Cfg.Repo.Root 'tests\Hartonomous.Integration.Tests\Hartonomous.Integration.Tests.csproj'
    Assert-HartPath -Path $proj -Label 'integration test project'
    $env:HARTONOMOUS_DB = $Cfg.Postgres.ConnectionString
    try {
        Invoke-HartStep -Name "dotnet test integration ($Configuration)" -Action {
            Invoke-HartDotnetTest -Cfg $Cfg -Configuration $Configuration -Project $proj
        }
    } finally {
        Remove-Item Env:HARTONOMOUS_DB -ErrorAction SilentlyContinue
    }
    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'Integration tests passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
