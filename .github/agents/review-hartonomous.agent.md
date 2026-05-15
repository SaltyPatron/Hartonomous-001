---
name: review-hartonomous
description: Review Hartonomous changes for substrate drift and implementation errors.
---

## Substrate violations to catch

Review from current repo truth, not old migration memory. For schema claims, inspect `sql/schema/bootstrap.sql` and the included `sql/schema/tables/`, `sql/schema/functions/`, and `sql/schema/seed/` files. Lead with bugs, drift, missing verification, and any stale docs/agent instructions that would cause the same error to reappear.

### Entity/edge/physicality conflation
- `substrate.entity` holds atoms and compositions ONLY. It is hash-only: `hash` is the primary key. POS labels, language codes, source paths, and entity types are reference or classification metadata, not entities.
- `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)` is the only place structural entity type classification belongs.
- `substrate.edge` + `substrate.edge_member` are separate n-ary typed relations. Edges are NOT entities. Edge data in the entity table or entity classification in edge tables = structural corruption.
- `substrate.physicality` stores per-entity `geometry(GeometryZM)`. Geometry stored outside physicality (other than edge trajectory `geom`) = wrong.
- Junction tables (`entity_pos`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc.) are evidence infrastructure, NOT edges.

### Identity hash corruption
- `ComputeHash()`: content bytes only. `ComputeMerkleHash()`: ordered child hashes. `ComputeEdgeHash()`: `(edgeTypeId, participantHashes)`.
- Position, ordinal, filename, tensor name, source offset in hash = corrupted. These live in the composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`), in `provenance`, or on typed edges (`has_source`, `in_model`, `edge_member.role_position`). There is no `substrate.sequence` table.
- Same content in two places = one entity with two edges, not two entities.

### Inference boundary
- Ingestion (`src/Hartonomous.Decomposers/`): deterministic, records all candidates. Same input + same version = same state.
- Inference (`src/Hartonomous.Engine/`): traverses + reweights existing edges via Glicko-2. Session-scoped outputs only. Does NOT create new knowledge edges.

### Determinism
- No HNSW, ANN, randomized SVD, quantization, stochastic estimation, random projection, LSH, Nyström.
- All PRNG: fixed seed from config/spec. MKL `CBWR=AUTO,STRICT`.
- Content bytes preserved. Gradient jitter not stored. Sparsity = honest recording.

### Compute facade bypass
- All compute: `IComputeFacade` → `ComputeFacade` → `NativeCompute` → `libhartonomous`.
- Direct imports of MKL/Eigen/Spectra/ONNX from decomposers/engine = violation.

### DB patterns
- Set-based: `INSERT ... SELECT FROM unnest()` or `COPY`. `NpgsqlCommand` inside `foreach` = prohibited.
- One transaction per batch. Junction table names allowlist-validated.

### Whole-surface review
- If the fix touched a stale schema assumption, review code, tests, docs, prompts, and agent instructions for the same assumption.
- If a run failed in one record kind, check producer emission, channel drain, temp-table COPY, INSERT-SELECT target, FK/order assumptions, and end-of-phase post-passes before calling it fixed.

### C# conventions
- One type per file. `I`+PascalCase, `Base`+PascalCase. `async Task` + `CancellationToken` on I/O.
- `Microsoft.Extensions.Logging` only. Structured properties. No `Console.WriteLine` in libraries.
- `DecomposerConfig.ConnectionString` is `required`. No Moq. Synthetic test data.
