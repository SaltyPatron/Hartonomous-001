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

Before implementing, build a context map: current file, relevant path instructions, canonical schema files for any database shape, existing tests, and the semantic regression cases if the change touches text, identity, inference, or infrastructure-versus-substrate boundaries. Keep a short issue ledger while working so a single compiler error does not hide adjacent failures.

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

Content only. Position/ordinal/filename/tensor name → composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`), edges (`has_source`, `in_model`, `edge_member.role_position`), or `provenance`. No `substrate.sequence` table. Natural-language text MUST go through `CanonicalTextDecomposer.Emit` — `ComputeAtomicStringHash` is for structured atomic identifiers only.

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
| `substrate.entity` | hash | not partitioned |
| `substrate.entity_classification` | entity_hash, entity_type_id, provenance_id | not partitioned |
| `substrate.edge` | edge_type_id, hash, geom, provenance_id | edge_type_id |
| `substrate.edge_member` | edge_type_id, edge_hash, entity_hash, edge_role_id, role_position | edge_type_id |
| `substrate.physicality` | physicality_type_id, entity_hash, content_hash, geom | physicality_type_id |
| `substrate.entity_significance` | context_type_id, entity_hash, mu, sigma, volatility, games | context_type_id |
| `substrate.edge_significance` | context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games | context_type_id |
| (composition ordering) | LIVES IN composition LINESTRINGZM physicality vertex Y mantissa via `bb_pack_ordinal_rle(ordinal, rle_count)`; NOT a separate table | n/a |

## Source locations

| Area | Path |
|------|------|
| Core | `src/Hartonomous.Core/Decomposition/` (IDecomposer, BaseDecomposer, DecomposerConfig) |
| Compute | `src/Hartonomous.Core/Compute/` (IComputeFacade, ComputeFacade, Blake3, Blake3Hasher) |
| Native | `src/Hartonomous.Core/Native/` (Blake3Native, S3Native, SuperFibonacciNative, HilbertNative) |
| Phases | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposers | `src/Hartonomous.Decomposers/` (Ucd/, Iso639/, WordNet/, Omw/, Ud/, Safetensors/, Wiktionary/, Tatoeba/) |
| Engine | `src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` |
| Canonical schema | `sql/schema/bootstrap.sql` include manifest plus source files under `sql/schema/`; runtime install uses generated extension SQL |

## Completion bar

Do not stop at the first fixed error. Check the adjacent path, update stale docs or agent scaffolding touched by the same assumption, run the narrowest meaningful test/build gate, and report residual risk explicitly.
