# Hartonomous.Verify — local static verification helpers.

function Get-HartRelativePath {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\\', '/')
}

function Get-HartLineNumberFromIndex {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [int]$Index
    )

    if ($Index -le 0) { return 1 }

    return ([regex]::Matches($Text.Substring(0, $Index), "`r?`n")).Count + 1
}

function Get-HartLineAtIndex {
    param(
        [Parameter(Mandatory)] [string]$Text,
        [Parameter(Mandatory)] [int]$Index
    )

    $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $Index))
    if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart++ }

    $lineEnd = $Text.IndexOf("`n", $lineStart)
    if ($lineEnd -lt 0) { $lineEnd = $Text.Length }

    return $Text.Substring($lineStart, $lineEnd - $lineStart).Trim()
}

function Get-HartInlineSqlFindings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Cfg,
        [ValidateSet('All','CSharp','Scripts')] [string]$Scope = 'All'
    )

    $findings = New-Object System.Collections.Generic.List[object]

    if ($Scope -in @('All', 'CSharp')) {
        $srcRoot = Join-Path $Cfg.Repo.Root 'src'
        if (Test-Path $srcRoot) {
            $csharpPattern = '(new\s+NpgsqlCommand\s*\(\s*(?:@?\$?"|"""))|((?:@?\$?"|""")\s*(SELECT|CALL|INSERT|UPDATE|DELETE|WITH|COPY|TRUNCATE)\b)|(CommandText\s*=\s*(?:@?\$?"|"""))'

            Get-ChildItem -LiteralPath $srcRoot -Recurse -File -Filter '*.cs' | ForEach-Object {
                $content = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop
                foreach ($match in [regex]::Matches($content, $csharpPattern)) {
                    $findings.Add([pscustomobject]@{
                        Scope = 'CSharp'
                        Path = Get-HartRelativePath -Root $Cfg.Repo.Root -Path $_.FullName
                        Line = Get-HartLineNumberFromIndex -Text $content -Index $match.Index
                        Rule = 'csharp-inline-sql'
                        Text = Get-HartLineAtIndex -Text $content -Index $match.Index
                    }) | Out-Null
                }
            }
        }
    }

    if ($Scope -in @('All', 'Scripts')) {
        $scriptsRoot = Join-Path $Cfg.Repo.Root 'scripts'
        if (Test-Path $scriptsRoot) {
            $scriptPattern = '(?is)\bInvoke-HartPsql(?:Scalar)?\b[\s\S]{0,500?}\s-Sql\b'
            $excludedScripts = @(
                'scripts/docker/Psql.ps1',
                'scripts/verify/NoInlineSql.ps1',
                'scripts/lib/Hartonomous.Verify.psm1'
            )

            Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File |
                Where-Object { $_.Extension -in @('.ps1', '.psm1') } |
                ForEach-Object {
                    $relativePath = Get-HartRelativePath -Root $Cfg.Repo.Root -Path $_.FullName
                    if ($relativePath -in $excludedScripts) { return }

                    $content = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop
                    foreach ($match in [regex]::Matches($content, $scriptPattern)) {
                        $findings.Add([pscustomobject]@{
                            Scope = 'Scripts'
                            Path = $relativePath
                            Line = Get-HartLineNumberFromIndex -Text $content -Index $match.Index
                            Rule = 'powershell-inline-sql'
                            Text = Get-HartLineAtIndex -Text $content -Index $match.Index
                        }) | Out-Null
                    }
                }
        }
    }

    return $findings
}

Export-ModuleMember -Function Get-HartInlineSqlFindings
