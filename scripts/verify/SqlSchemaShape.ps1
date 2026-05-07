#requires -Version 7
<#
.SYNOPSIS
  Verify canonical sql/schema layout and one-object-per-file shape.

.DESCRIPTION
  Hartonomous pre-v1 schema source lives under sql/schema and is assembled by
  sql/schema/bootstrap.sql. This verifier fails when a canonical schema file
  contains zero or multiple primary object definitions, when the source file
  name does not match the object name, or when bootstrap includes are stale.

.PARAMETER MaxFindings
  Maximum detailed findings to print before truncating output.

.EXAMPLE
  pwsh scripts/verify/SqlSchemaShape.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 10000)] [int]$MaxFindings = 200
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'verify.SqlSchemaShape' -Cfg $Cfg

function ConvertTo-RepoPath {
    param([Parameter(Mandatory)] [string]$Path)
    return [System.IO.Path]::GetRelativePath($Cfg.Repo.Root, $Path).Replace('\', '/')
}

function Remove-SqlComments {
    param([Parameter(Mandatory)] [string]$Sql)

    $withoutBlock = [regex]::Replace($Sql, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    return [regex]::Replace($withoutBlock, '(?m)^\s*--.*$', '')
}

function Normalize-SqlIdentifier {
    param([Parameter(Mandatory)] [string]$Identifier)

    $parts = $Identifier -split '\s*\.\s*'
    $name = $parts[$parts.Count - 1].Trim()
    if ($name.StartsWith('"') -and $name.EndsWith('"')) {
        $name = $name.Substring(1, $name.Length - 2)
    }
    return $name.ToLowerInvariant()
}

function Get-PrimarySqlObjects {
    param([Parameter(Mandatory)] [string]$Sql)

    $clean = Remove-SqlComments -Sql $Sql
    $pattern = '(?im)^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?<kind>DOMAIN|TYPE|TABLE|FUNCTION|PROCEDURE|VIEW|SCHEMA|EXTENSION|INDEX|TRIGGER|AGGREGATE)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:"[^"]+"|[a-z_][a-z0-9_]*)(?:\s*\.\s*(?:"[^"]+"|[a-z_][a-z0-9_]*))?)\b'
    foreach ($match in [regex]::Matches($clean, $pattern)) {
        [pscustomobject]@{
            Kind = $match.Groups['kind'].Value.ToUpperInvariant()
            Name = Normalize-SqlIdentifier -Identifier $match.Groups['name'].Value
        }
    }
}

function Get-ExpectedObjectKinds {
    param([Parameter(Mandatory)] [string]$RelativePath)

    if ($RelativePath -like 'sql/schema/domains/*') { return @('DOMAIN') }
    if ($RelativePath -like 'sql/schema/types/*') { return @('TYPE') }
    if ($RelativePath -like 'sql/schema/tables/*') { return @('TABLE') }
    if ($RelativePath -like 'sql/schema/indexes/*') { return @('INDEX') }
    if ($RelativePath -like 'sql/schema/functions/*') { return @('FUNCTION') }
    if ($RelativePath -like 'sql/schema/procedures/*') { return @('PROCEDURE') }
    if ($RelativePath -like 'sql/schema/views/*') { return @('VIEW') }
    if ($RelativePath -like 'sql/schema/schemas/*') { return @('SCHEMA') }
    if ($RelativePath -like 'sql/schema/extensions/*') { return @('EXTENSION') }
    return @()
}

function Test-ShouldHavePrimaryObject {
    param([Parameter(Mandatory)] [System.IO.FileInfo]$File)

    $relative = ConvertTo-RepoPath -Path $File.FullName
    if ($relative -eq 'sql/schema/bootstrap.sql') { return $false }
    if ($relative -like 'sql/schema/seed/*') { return $false }
    return $true
}

try {
    $schemaRoot = Join-Path $Cfg.Repo.Root 'sql/schema'
    $bootstrap = Join-Path $schemaRoot 'bootstrap.sql'
    $findings = New-Object System.Collections.Generic.List[object]

    $includePattern = '@include\s+(schema/\S+)'
    $included = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $bootstrap -Raw), $includePattern)) {
        $include = $match.Groups[1].Value.Replace('\', '/')
        $null = $included.Add($include)
        $full = Join-Path $Cfg.Repo.Root (Join-Path 'sql' $include)
        if (-not (Test-Path -LiteralPath $full)) {
            $findings.Add([pscustomobject]@{
                Path = 'sql/schema/bootstrap.sql'
                Rule = 'missing-include-target'
                Message = "Bootstrap include target does not exist: $include"
            }) | Out-Null
        }
    }

    $files = Get-ChildItem -LiteralPath $schemaRoot -Recurse -File -Filter '*.sql' |
        Where-Object { $_.FullName -notlike '*\migrations.archive\*' }

    foreach ($file in $files) {
        $relative = ConvertTo-RepoPath -Path $file.FullName
        if ($relative -ne 'sql/schema/bootstrap.sql') {
            $includePath = $relative.Substring(4).Replace('\', '/')
            if (-not $included.Contains($includePath)) {
                $findings.Add([pscustomobject]@{
                    Path = $relative
                    Rule = 'not-in-bootstrap'
                    Message = 'Canonical schema SQL file is not included by sql/schema/bootstrap.sql.'
                }) | Out-Null
            }
        }

        if (-not (Test-ShouldHavePrimaryObject -File $file)) { continue }

        $objects = @(Get-PrimarySqlObjects -Sql (Get-Content -LiteralPath $file.FullName -Raw))
        if ($objects.Count -ne 1) {
            $findings.Add([pscustomobject]@{
                Path = $relative
                Rule = 'one-object-per-file'
                Message = "Expected exactly one primary CREATE object; found $($objects.Count)."
            }) | Out-Null
            continue
        }

        $expectedKinds = @(Get-ExpectedObjectKinds -RelativePath $relative)
        if ($expectedKinds.Count -gt 0 -and $objects[0].Kind -notin $expectedKinds) {
            $findings.Add([pscustomobject]@{
                Path = $relative
                Rule = 'folder-object-kind'
                Message = "Object kind $($objects[0].Kind) does not belong in this schema folder. Expected: $($expectedKinds -join ', ')."
            }) | Out-Null
        }

        $expectedName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name).ToLowerInvariant()
        if ($objects[0].Name -ne $expectedName -and -not $expectedName.EndsWith("_$($objects[0].Name)", [StringComparison]::OrdinalIgnoreCase)) {
            $findings.Add([pscustomobject]@{
                Path = $relative
                Rule = 'filename-object-name'
                Message = "File name '$expectedName' does not match $($objects[0].Kind.ToLowerInvariant()) '$($objects[0].Name)'."
            }) | Out-Null
        }
    }

    if ($findings.Count -gt 0) {
        Write-HartError "SQL schema shape drift detected: $($findings.Count) finding(s)."
        foreach ($finding in ($findings | Select-Object -First $MaxFindings)) {
            Write-HartError ("{0} [{1}] {2}" -f $finding.Path, $finding.Rule, $finding.Message)
        }
        if ($findings.Count -gt $MaxFindings) {
            Write-HartError "... $($findings.Count - $MaxFindings) more finding(s) omitted."
        }
        Exit-Hartonomous -Code $Cfg.ExitCodes.DataError
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'SQL schema shape check passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
