$context = @'
Hartonomous execution overlay:
- Compute measurable facts exactly from repo or data when tools can provide the answer.
- Treat terse examples like overload, highrise, minute, POS/sense disputes, and identity/reconstruction disputes as live semantic regression cases, not as prompts for generic taxonomy.
- Preserve the core substrate split: one entity table for atoms and compositions, separate n-ary edge substrate, one universal physicality table, and reference/junction infrastructure outside the entity and edge substrate.
- BLAKE3 identity hashes cover content only. Sequence, placement, provenance, and reconstruction metadata live on edges, geometry, or other metadata surfaces.
- Inference traverses and reweights existing edges; it does not invent new edges.
- Prefer documented repo entrypoints under scripts/ for build, test, db, docker, and seed operations.
- When you add or remove standards docs, keep docs/index.md and docs/standards/README.md truthful.
- If semantics are in dispute, consult .claude/skills/hartonomous-semantic-eval/cases.md and rubric.md before implementing or reviewing.
'@

$payload = [ordered]@{
    hookSpecificOutput = [ordered]@{
        hookEventName    = 'SessionStart'
        additionalContext = $context.Trim()
    }
}

$payload | ConvertTo-Json -Compress -Depth 8
