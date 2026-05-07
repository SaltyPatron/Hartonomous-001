#requires -Version 7
<#
.SYNOPSIS
  Fail when source or ops scripts contain inline SQL command bodies.

.DESCRIPTION
  Hartonomous database access goes through named SQL contracts and typed
  repository/script surfaces. This verifier keeps inline SQL visible as a
  build failure instead of allowing ad hoc SELECT/CALL/INSERT text to drift
  into C# or normal ops scripts.

.PARAMETER Scope
  Select which surfaces to scan: All, CSharp, or Scripts.

.PARAMETER MaxFindings
  Maximum detailed findings to print before truncating output.

.EXAMPLE
  pwsh scripts/verify/NoInlineSql.ps1
  pwsh scripts/verify/NoInlineSql.ps1 -Scope CSharp -MaxFindings 50
#>
[CmdletBinding()]
param(
    [ValidateSet('All','CSharp','Scripts')] [string]$Scope = 'All',
    [ValidateRange(1, 10000)] [int]$MaxFindings = 200
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force
Import-Module "$PSScriptRoot\..\lib\Hartonomous.Verify.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'verify.NoInlineSql' -Cfg $Cfg

try {
    $findings = @(Get-HartInlineSqlFindings -Cfg $Cfg -Scope $Scope)

    if ($findings.Count -eq 0) {
        Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message "No inline SQL findings for scope '$Scope'."
    }

    Write-HartError "Inline SQL detected: $($findings.Count) finding(s) for scope '$Scope'."
    $findings |
        Group-Object Scope |
        Sort-Object Name |
        ForEach-Object { Write-HartError ("{0}: {1}" -f $_.Name, $_.Count) }

    foreach ($finding in ($findings | Select-Object -First $MaxFindings)) {
        Write-HartError ("{0}:{1} [{2}] {3}" -f $finding.Path, $finding.Line, $finding.Rule, $finding.Text)
    }

    if ($findings.Count -gt $MaxFindings) {
        Write-HartError "... $($findings.Count - $MaxFindings) more finding(s) omitted."
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.DataError
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
