# Hartonomous Copilot Instructions

The root `CLAUDE.md` file remains the full authoritative standards document for this repository. These instructions are the concise always-on Copilot overlay.

## Substrate invariants — preserve exactly

Hartonomous is an invention-specific substrate, not a generic knowledge graph, vector database, RAG stack, or approximate embedding system. These are non-negotiable:

- **One entity table** (`substrate.entity`, migration `0006`) for atoms and compositions only. Identity = BLAKE3 hash of content via `BaseDecomposer.ComputeHash()`. Compositions use Merkle hashing via `ComputeMerkleHash()`.
- **Separate n-ary edge substrate** (`substrate.edge` + `substrate.edge_member`, migration `0006`) with role-ordered participants, trajectory geometry (`geom` column), and Glicko-2 significance. Edges are NOT entities.
- **One universal physicality table** (`substrate.physicality`, migration `0006`) for geometry across all modalities. POINTZM for atoms, LINESTRINGZM for compositions. GiST-indexed. `ST_FrechetDistance` compares shapes.
- **Classification vocabularies** in reference tables (`pos`, `deprel`, `sense`, `language`, etc. — migration `0004`) and junction tables (`entity_pos`, `entity_sense`, etc. — migration `0007`). NOT in the entity or edge substrate.
- **BLAKE3 identity hashes** cover content only, never placement metadata (position, filename, ordinal, tensor name). Placement lives on `sequence.position`, edges (`has_source`, `in_model`), or `provenance`.
- **Inference** (`src/Hartonomous.Engine/`) traverses and reweights existing edges via Glicko-2 significance. It does NOT invent new knowledge edges. **Ingestion** (`src/Hartonomous.Decomposers/`) is deterministic — same input + same decomposer version = same substrate state.

## Semantic regression cases

The 10 regression cases in `.claude/skills/hartonomous-semantic-eval/cases.md` cover: #1 one form many senses (`overload`), #2 lexicalized compounds (`highrise`), #3 time-varying POS (`minute`), #4 cross-lingual alignment, #5 decomposition levels, #6 infrastructure vs content, #7 identity vs reconstruction, #8 inference vs ingestion, #9 model weight sparsity, #10 terse examples as substrate probes.

## Exact counts

24 migration pairs (0001–0024). 12 phases in the Phase enum. 9 decomposers. 25 entity types. 33 edge types. 7 edge roles. 13 physicality types. 10 significance arenas. 10 provenances. 8 junction tables (3 with Glicko-2).

## Repo entrypoints

| Task | Script |
|------|--------|
| Build all | `scripts/build/All.ps1` |
| Build .NET | `scripts/build/Dotnet.ps1` |
| Build native | `scripts/build/Native.ps1` |
| Test all | `scripts/test/All.ps1` |
| Test .NET | `scripts/test/Dotnet.ps1` |
| Test integration | `scripts/test/Integration.ps1` |
| Test native | `scripts/test/Native.ps1` |
| DB migrate | `scripts/db/Migrate.ps1` |
| DB reset | `scripts/db/Reset.ps1` |
| Docker up | `scripts/docker/Up.ps1` |
| Docker down | `scripts/docker/Down.ps1` |
| Seed all | `scripts/seed/All.ps1` |
| Run phases | `scripts/ops/Phases.ps1` |

## Key code locations

| Area | Path |
|------|------|
| Core abstractions | `src/Hartonomous.Core/Decomposition/` (IDecomposer, BaseDecomposer, DecomposerConfig) |
| Compute facade | `src/Hartonomous.Core/Compute/` (IComputeFacade, ComputeFacade, Blake3, Blake3Hasher) |
| Native P/Invoke | `src/Hartonomous.Core/Native/` (Blake3Native, S3Native, SuperFibonacciNative, HilbertNative) |
| Phase orchestration | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposers | `src/Hartonomous.Decomposers/` (Ucd, Iso639, WordNet, Omw, Ud, Safetensors, Wiktionary, Tatoeba) |
| Engine | `src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` |
| Migrations | `sql/migrations/` (0001–0024) |

## Supplementary instruction surfaces

- Path-specific rules: `.github/instructions/hartonomous-{csharp,sql,native,docs}.instructions.md`
- Claude-format rules: `.claude/rules/*.md` (5 files covering core, text/semantics, SQL/ingestion, native/determinism, docs/config)
- Semantic regression pack: `.claude/skills/hartonomous-semantic-eval/` (SKILL.md, cases.md, rubric.md)
- Agents: `.github/agents/` (plan, implement, review, semantic-auditor) with handoff chains
- Prompts: `.github/prompts/semantic-eval.prompt.md`, `.github/prompts/finish-work.prompt.md`

## Documentation maintenance

If standards docs are added or removed, update `docs/index.md` and `docs/standards/README.md` in the same change.
