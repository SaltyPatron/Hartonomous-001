---
name: implement-hartonomous
description: Implement Hartonomous work end-to-end.
handoffs:
  - label: Review Changes
    agent: review-hartonomous
    prompt: Review the completed changes.
    send: false
---

## C# conventions

- One type per file. File name = type name. `I`+PascalCase interfaces. `Base`+PascalCase abstract.
- All I/O: `async Task` + `CancellationToken`. Pure compute: synchronous.
- `Microsoft.Extensions.Logging` only. Structured: `{EntityCount}`. Trace (per-entity), Debug (per-batch), Information (phase start/end).
- DB: `INSERT ... SELECT FROM unnest($1, $2)` or `COPY ... FROM STDIN (FORMAT binary)`. Never `NpgsqlCommand` inside `foreach`. One transaction per batch.
- `DecomposerConfig.ConnectionString` is `required`. No defaults in library code.
- xUnit + coverlet. Hand-written fakes. Synthetic data.
- `Result<T>` for expected failures. Exceptions for bugs — propagate up.

## Hashing

```csharp
public static byte[] ComputeHash(ReadOnlySpan<byte> content) => Blake3.Hash(content);
public static byte[] ComputeAtomicStringHash(string atomicIdentifier) => Blake3.Hash(Encoding.UTF8.GetBytes(atomicIdentifier).AsSpan()); // structured tokens only (synset offsets, ISO 639 codes); never user-visible text
public static byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes) // concat → Merkle.Hash()
protected static byte[] ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<byte[]> participantHashes) // [4B type | hashes] → ComputeHash()
```

Content only. Position/ordinal/filename/tensor name → `sequence`, edges, `provenance`. Natural-language text MUST go through `CanonicalTextDecomposer.Emit` — `ComputeAtomicStringHash` is for structured atomic identifiers only.

## Compute facade

`IComputeFacade` → `ComputeFacade` → `NativeCompute` (`src/Hartonomous.Core/Compute/Internal/NativeCompute.cs`) → `libhartonomous` (`ext/libhartonomous/`).
P/Invoke: `Blake3Native`, `S3Native`, `SuperFibonacciNative`, `HilbertNative` in `src/Hartonomous.Core/Native/`.
Sub-modules: `Compute.Ingestion.*`, `Compute.Inference.*`, `Compute.Common.*`.
No direct MKL/Eigen/Spectra imports from decomposers/engine/analysis.

## Decomposer contract

```csharp
public interface IDecomposer : IAsyncDisposable {
    string ProvenanceCode { get; }
    string DisplayName { get; }
    IReadOnlyList<Phase> Phases { get; }
    Task ValidateSourceAsync(CancellationToken ct);
    Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct);
}
```

`BaseDecomposer` provides `DecomposeCoreAsync()` (abstract), `ValidateSourceAsync()` (source path check), `SubmitAndReportAsync()` (submit + report). Progress via `IProgressReporter`.

## Schema quick reference

| Table | Key columns | Partitioned by |
|-------|------------|----------------|
| `substrate.entity` | id, hash, entity_type_id | entity_type_id (25 types) |
| `substrate.edge` | id, hash, edge_type_id, geom, provenance_id | edge_type_id (33 types) |
| `substrate.edge_member` | edge_id, entity_id, edge_role_id | unpartitioned |
| `substrate.physicality` | id, entity_id, physicality_type_id, geom | physicality_type_id (13 types) |
| `substrate.significance` | id, entity_id\|edge_id, context_type_id, mu, sigma, volatility, games | context_type_id (10 arenas) |
| `substrate.sequence` | id, parent_id, child_id, ordinal_position, rle_count | unpartitioned |

## Source locations

| Area | Path |
|------|------|
| Core | `src/Hartonomous.Core/Decomposition/` (IDecomposer, BaseDecomposer, DecomposerConfig) |
| Compute | `src/Hartonomous.Core/Compute/` (IComputeFacade, ComputeFacade, Blake3, Blake3Hasher) |
| Native | `src/Hartonomous.Core/Native/` (Blake3Native, S3Native, SuperFibonacciNative, HilbertNative) |
| Phases | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposers | `src/Hartonomous.Decomposers/` (Ucd/, Iso639/, WordNet/, Omw/, Ud/, Safetensors/, Wiktionary/, Tatoeba/) |
| Engine | `src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` |
| Migrations | `sql/migrations/` (0001–0035, next = 0036) |
