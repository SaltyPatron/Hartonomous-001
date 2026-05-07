#requires -Version 7
<#
.SYNOPSIS
  Scan AI-facing scaffolding for stale migration-era Hartonomous assumptions.

.DESCRIPTION
  This is a cheap guardrail for the exact failure mode that makes agents lose
  the invention: durable prompt/agent/docs surfaces silently retaining old
  schema facts. It scans tracked AI scaffolding and standards files for
  phrases that imply the retired migration-era entity/significance model.

.PARAMETER IncludeIgnoredClaude
  Also scan the local .claude/ tree when it exists. That tree is ignored by
  git in this workspace, so CI does not rely on it.

.EXAMPLE
  pwsh scripts/verify/AgentScaffolding.ps1
  pwsh scripts/verify/AgentScaffolding.ps1 -IncludeIgnoredClaude
#>
[CmdletBinding()]
param(
    [switch]$IncludeIgnoredClaude
)

Import-Module "$PSScriptRoot\..\lib\Hartonomous.Common.psm1" -Force

$Cfg = Get-HartonomousConfig
Start-HartonomousLog -ScriptName 'verify.AgentScaffolding' -Cfg $Cfg

function Get-ScanFiles {
    param([string]$Root)

    $paths = @(
        '.github',
        'CLAUDE.md',
        'docs/standards',
        'scripts/README.md'
    )

    if ($IncludeIgnoredClaude -and (Test-Path (Join-Path $Root '.claude'))) {
        $paths += '.claude'
    }

    foreach ($relative in $paths) {
        $full = Join-Path $Root $relative
        if (-not (Test-Path $full)) { continue }

        $item = Get-Item -LiteralPath $full -Force
        if (-not $item.PSIsContainer) {
            $item
            continue
        }

        Get-ChildItem -LiteralPath $full -Recurse -File -Force |
            Where-Object { $_.Extension -in @('.md', '.yml', '.yaml', '.json') }
    }
}

$rules = @(
    @{ Id = 'active-migrations-dir'; Pattern = 'sql[\\/]migrations(?!\.archive)'; Message = 'Active schema path must be sql/schema/ or generated extension SQL, not sql/migrations/.' },
    @{ Id = 'migrate-workflow'; Pattern = '\bmigrate (up|down|status)\b|\bmigrations? job\b|idempotent migrations'; Message = 'Pre-v1 workflow is build extension SQL + bootstrap, not migrate up/down/status.' },
    @{ Id = 'old-unified-significance'; Pattern = '\bsubstrate\.significance\b'; Message = 'Use substrate.entity_significance or substrate.edge_significance.' },
    @{ Id = 'removed-sense-junction'; Pattern = '\bentity_sense\b'; Message = 'Sense evidence is represented by typed edges and edge_significance, not entity_sense.' },
    @{ Id = 'removed-text-entity-type'; Pattern = '\b(word_sense|wikt_sense|ud_sentence|ud_token|tatoeba_sentence|inflected_form)\b'; Message = 'Removed text entity type from migration-era schema.' },
    @{ Id = 'old-counts'; Pattern = '\b25\s+(entity|rows?)\b|\b33\s+edge\b|\b13\s+physicality\b'; Message = 'Counts must be recomputed from sql/schema seeds; current scaffold should not cite old counts.' },
    @{ Id = 'old-sequence-columns'; Pattern = '\bsequence\.(position|ordinal_position)\b|\borderinal_position\s+INT\b'; Message = 'Use substrate.sequence.ordinal.' },
    @{ Id = 'old-entity-shape'; Pattern = 'INSERT\s+INTO\s+(substrate\.)?entity\s*\(\s*hash\s*,\s*entity_type_id|RETURNING\s+id|partitioned\s+by\s+entity_type_id|PK\s*\(\s*id\b'; Message = 'substrate.entity is hash-only; classification is separate.' },
    @{ Id = 'old-extension-name'; Pattern = 'CREATE\s+EXTENSION\s+(IF\s+NOT\s+EXISTS\s+)?hartonomous_pg\b'; Message = 'The PostgreSQL extension name is hartonomous.' }
)

try {
    $findings = New-Object System.Collections.Generic.List[object]

    foreach ($file in Get-ScanFiles -Root $Cfg.Repo.Root) {
        $relativePath = [System.IO.Path]::GetRelativePath($Cfg.Repo.Root, $file.FullName).Replace('\', '/')
        $lines = Get-Content -LiteralPath $file.FullName -ErrorAction Stop

        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($rule in $rules) {
                if ($lines[$i] -match $rule.Pattern) {
                    $findings.Add([pscustomobject]@{
                        Path = $relativePath
                        Line = $i + 1
                        Rule = $rule.Id
                        Message = $rule.Message
                        Text = $lines[$i].Trim()
                    }) | Out-Null
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        Write-HartError "AI scaffolding drift detected: $($findings.Count) finding(s)."
        foreach ($finding in $findings) {
            Write-HartError ("{0}:{1} [{2}] {3}" -f $finding.Path, $finding.Line, $finding.Rule, $finding.Message)
            Write-HartError ("  {0}" -f $finding.Text)
        }
        Exit-Hartonomous -Code $Cfg.ExitCodes.DataError
    }

    Exit-Hartonomous -Code $Cfg.ExitCodes.Ok -Message 'AI scaffolding drift check passed.'
}
catch {
    Write-HartError $_.Exception.Message
    Exit-Hartonomous -Code $Cfg.ExitCodes.GenericError
}
