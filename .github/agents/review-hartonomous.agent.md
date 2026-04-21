---
name: review-hartonomous
description: Review Hartonomous changes for substrate drift and implementation errors.
---

## Substrate violations to catch

### Entity/edge/physicality conflation
- `substrate.entity` holds atoms and compositions ONLY. 25 entity types (migration `0005`). POS labels, language codes, source paths are reference vocabulary (migration `0004`), not entities.
- `substrate.edge` + `substrate.edge_member` — 33 edge types, 7 roles. Edges are NOT entities. Edge data in the entity table or vice versa = structural corruption.
- `substrate.physicality` — 13 types. Per-entity geometry. Geometry stored outside physicality (other than edge trajectory `geom`) = wrong.
- Junction tables (`entity_pos`, `entity_sense`, `entity_language`, `codepoint_property`, etc.) are evidence infrastructure, NOT edges.

### Identity hash corruption
- `ComputeHash()`: content bytes only. `ComputeMerkleHash()`: ordered child hashes. `ComputeEdgeHash()`: `(edgeTypeId, participantHashes)`.
- Position, ordinal, filename, tensor name, source offset in hash = corrupted. These live on `sequence.ordinal_position`, `provenance`, edges (`has_source`, `in_model`).
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

### C# conventions
- One type per file. `I`+PascalCase, `Base`+PascalCase. `async Task` + `CancellationToken` on I/O.
- `Microsoft.Extensions.Logging` only. Structured properties. No `Console.WriteLine` in libraries.
- `DecomposerConfig.ConnectionString` is `required`. No Moq. Synthetic test data.
