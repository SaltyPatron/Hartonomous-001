# Baseline — Verified state for V1 implementation

Last captured from the user's PowerShell session on 2026-05-01 against the
prior numbered-migrations pipeline. The substrate has since moved to
**bootstrap-only** (pre-v1 has no migration history; `sql/schema/` is the
canonical source of truth, applied via `scripts/db/Bootstrap.ps1`).

## Apply path (current)

```pwsh
.\scripts\Docker\Down.ps1 -RemoveVolumes -Force
.\scripts\Docker\Build.ps1
.\scripts\Docker\Up.ps1 -Rebuild
.\scripts\build\Dotnet.ps1
.\scripts\db\Drop.ps1 -Force
.\scripts\db\Create.ps1
.\scripts\db\Bootstrap.ps1
```

`RunAll.bat` wraps the above plus the seed corpora.

`Bootstrap.ps1` invokes the CLI `bootstrap` subcommand, which expands the
`@include` directives in `sql/schema/bootstrap.sql` recursively and applies
the result in a single transaction. Edit canonical files in place; reseed
re-applies them. There is no migration ledger, no `schema_version` table,
no checksum drift.

The `sql/migrations/` directory has been retired to
`sql/migrations.archive/_v2_pre_bootstrap/` for historical reference.

## Build (last known good)

`dotnet build`: clean across all 13 projects on Debug. Recheck after the
recomposer / decomposer changes added since 2026-05-01.

## Docker stack

- `docker compose -p hartonomous down -v` works; volumes destroyed.
- Build images: `hartonomous/postgres:18.3`, `hartonomous/postgis:3.6.3`,
  `hartonomous/pgext:dev`, `hartonomous-postgres:latest`.
- `docker compose up -d --build` reports container `hartonomous-postgres`
  healthy in ~6 s.

## Database setup

- `db/Drop.ps1 -Force` drops the database after terminating connections.
- `db/Create.ps1` creates the database and ensures `postgis` and
  `hartonomous` extensions.
- `db/Bootstrap.ps1` applies the canonical substrate schema:
  extensions, schemas, domains, composite types, reference tables, seed,
  core tables (entity / edge / edge_member / physicality / sequence /
  significance), junction tables, model tables, monitor tables, meta
  tables, staging tables, functions (reference / geometry / read-side /
  composition / significance / staging-drain / inference / universal
  query), monitor procedures, views, and the `hartonomous` C extension.

## Substrate completeness

`sql/tests/schema_completeness_tests.sql` (run via `scripts/test/Brain.ps1`)
verifies after bootstrap:
- 2 schemas: `substrate`, `monitor`.
- 4 extensions: `postgis`, `btree_gist`, `pg_trgm`, `hartonomous`.
- 8 substrate domains.
- 18 reference tables, 7 core substrate tables, 10 junction tables,
  9 staging tables, 5 model tables, 8 monitor tables.
- ~40 substrate functions, 6 monitor procedures.
- Arena machinery round-trip: `create_arena` is idempotent;
  `create_model_trust_arena` materializes `model_trust:<provenance>`.

Seed counts (asserted at the end of bootstrap by `seed/validate.sql`):
54 entity_types, 13 physicality_types, 7 edge_roles,
10 significance_contexts, 10 provenances, 45 lexnames, 17 POS, 111 edge_types.

## Seed corpora

Run after bootstrap, on a clean substrate. Last verified row counts
(2026-05-01) for an in-progress run:
- **UCD/UCA** — `seed/Ucd.ps1 -SourceRoot D:\Models` — 43.8 s.
- **ISO 639** — `seed/Iso639.ps1 -SourceRoot D:\Models` — 8.0 s.
- **WordNet/OMW** — `seed/WordNetOmw.ps1 -SourceRoot D:\Models` — running.
- Pending in that capture: Universal Dependencies, Wiktionary, Tatoeba.
- No AI model ingestion in the baseline run.

## What this confirms

1. The codebase compiles cleanly when last checked end-to-end.
2. The Docker stack rebuilds and runs. PostgreSQL 18 + PostGIS 3.6 + the
   `hartonomous` extension operate on port 5433.
3. The bootstrap apply path produces a substrate that satisfies the
   completeness tests on a fresh DB.
4. Seed-corpora ingestion runs (UCD/ISO 639 confirmed; WordNet/OMW/UD/
   Wiktionary/Tatoeba require fresh capture after the bootstrap cutover).

## Open V1 work

The V1 product (custom-model construction from the universal substrate)
still requires:
- **Decomposer rewrite (V1 plan Phase 2):** architecture handlers
  (decoder-only / MoE / vision / VL / audio / AL / diffusion / reranker /
  LoRA), `ArchitectureEdgesPass`, `TokenizerCompositionPass`,
  `QuantizationVariantPass`, `LoraAdapterPass`, `FireflyConsensusPass`,
  `MultiComponentPass`, multi-tier `EmbeddingFireflyPass`,
  `NativeEmbeddingPass`. None are present.
- **Recomposer pieces (V1 plan Phase 4):** `PerRoleProjection`,
  `QuantizationConvert`, `LoraExport`, `MultiComponentRecompose`,
  `SubstrateStateMerkle`. The recipe DSL, audit-chain `__metadata__`,
  sharding, recipe content hash, and filtered/sharded export paths are
  in place.
- **Test coverage (V1 plan Phase 5):** `RecomposeCorrectnessTests`,
  `RefinementIntelligenceTests`. Other vertical-slice scaffolds and
  fixtures exist.

These are the targets for finishing V1.
