---
name: Hartonomous Docs Rules
description: Documentation and repo-truthfulness rules for Hartonomous markdown docs.
applyTo: 'docs/**/*.md'
---

## Documentation tree

| Path | Purpose |
|------|---------|
| `docs/architecture.md` | Substrate laws (1–13), schema tables, scale, "What This Is NOT" |
| `docs/build-plan.md` | Implementation roadmap and milestone tracking |
| `docs/flow-inventory.md` | Data flow descriptions per phase |
| `docs/glossary.md` | Term definitions specific to Hartonomous |
| `docs/index.md` | Master index of all documentation with status markers |
| `docs/type-system.md` | Entity type codes and type hierarchy |
| `docs/specs/` | Detailed specifications by area |
| `docs/standards/` | Coding and operational standards |
| `docs/standards/README.md` | Index of standards documents |

## Status markers

`docs/index.md` uses status markers to track documentation completeness. When updating docs, keep the markers truthful. Do not claim a document is complete without verifying its content.

## Update procedures

1. **Adding a document**: create the file, add it to `docs/index.md` with correct status.
2. **Removing a document**: delete the file, remove from `docs/index.md`, and remove from `docs/standards/README.md` if it was a standards doc.
3. **Adding a standards doc**: create the file in `docs/standards/`, add to both `docs/index.md` and `docs/standards/README.md`.
4. **Changing AI scaffolding**: update `docs/standards/ai-agent-workflows.md` if the change affects agent structure, skill packs, or hook policy.

## Truthfulness rules

- Keep documentation aligned with actual repo state. Do not claim completeness or counts without checking them.
- If a count, total, inventory, or status depends on the repo, compute it exactly (e.g., count `INSERT` tuples in `sql/schema/seed/entity_type.sql` or enumerate files under `sql/schema/tables/junctions/`).
- Preserve invention-specific distinctions. Do not translate Hartonomous concepts into generic AI terminology (knowledge graph, vector database, RAG, semantic search, embedding).
- The "What This Is NOT" section in `docs/architecture.md` enumerates the specific anti-patterns that documentation must never adopt.

## Spec documents

| Subdirectory | Covers |
|-------------|--------|
| `docs/specs/csharp/` | C# implementation details |
| `docs/specs/decomposers/` | Per-decomposer specifications |
| `docs/specs/engine/` | Inference engine specification |
| `docs/specs/modalities/` | Text, audio, image, video, model decomposition |
| `docs/specs/native/` | Native library and PG extension specs |
| `docs/specs/operations/` | Operational procedures |
| `docs/specs/sql/` | Schema and migration specifications |
