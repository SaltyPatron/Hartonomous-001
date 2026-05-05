# Ingestion Pipeline

> **⚠️ STALE — full rewrite pending.** The architecture below describes the pre-`0ce4e5e` design (persistent `substrate.staging_*` tables + `StagingFlushWorker` + `BackgroundSignificancePrimer` + `populate_edge_trajectories` post-pass). Commit `0ce4e5e` (2026-05-03) **deleted all of those**. The current architecture lives in `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` doc-comment (lines 17-56) and is summarized in `.claude/rules/00-hartonomous-core.md` § "Ingestion pipeline." Until this doc is rewritten, **the rule file is canonical**; this doc is historical reference only.
>
> Concretely, the new architecture: 10 bounded `Channel<TRecord>`, 10 per-kind drain tasks each with a long-lived `NpgsqlConnection`, per-chunk `TRUNCATE pg_temp.X_inflight` → `COPY pg_temp.X_inflight FROM STDIN BINARY` (≤4096 rows) → `INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING` within the same connection. Significance emitted inline. Edge LINESTRINGZM built inline in C# from participant centroids. No persistent staging schema; temp tables auto-drop with the connection.

**Status (HISTORICAL)**: ✅ Streaming pipeline (record-flow + persistent staging + background drain) operational. The legacy per-batch `NpgsqlIngestionPipeline` is retained as a deprecated shim while decomposer migrations land (tasks E1–E9).

The bridge between every decomposer (modality or seed) and the substrate. One centralized streaming pipeline owns:
- per-kind bounded channels for backpressure-controlled flow
- long-lived `NpgsqlBinaryImporter` COPY streams into persistent staging tables (REMOVED — see banner above)
- background drain of `substrate.staging_*` → `substrate.*` via `substrate.drain_staging_*_chunk` SQL functions (REMOVED)
- background priming of `substrate.edge_significance` via `substrate.prime_unprimed_edges_chunk` (REMOVED — significance now emitted inline)

Every decomposer is a pure record producer. There is no per-batch transaction, no per-batch staging-and-flush dance, no synchronous significance prime call inside the producer path.

---

## Invariant

**The pipeline is invention-level infrastructure. Decomposers are adapters.**

There is exactly one ingestion pipeline per phase run. Every decomposer — text, image, audio, video, telemetry, chess games, DNA, medical data, safetensors models, UCD, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba — emits records into that pipeline and nothing else. No decomposer owns a transaction. No decomposer owns a channel. No decomposer calls `ResolveEntityIdsAsync` to stitch its own cross-batch joins. No decomposer runs a "pass 2" over accumulated hashes.

Two classes of decomposer, both producers, no architectural difference:
1. **Modality (core) decomposers** — ingest native content of a modality. Text, image, audio, video, telemetry, chess PGN, DNA FASTA, medical DICOM, safetensors weights, etc. These OWN the AST decomposition for their modality (e.g., the text decomposer owns codepoint → grapheme_cluster → morpheme → word_form → text_composition → paragraph → document).
2. **Seed decomposers** — ingest authoritative foundational lexicons so the substrate has structural grammar to reason against. UCD/UCA, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba.

### Seed decomposers USE core decomposers — they do not bypass them

A Tatoeba sentence is NOT a flat `tatoeba_sentence` atom that carries the raw string. It is a full text AST produced by the TEXT core decomposer: codepoint → grapheme_cluster → morpheme → word_form → text_composition (Merkle) → paragraph → document. Each level is an entity with its own BLAKE3 content hash. The Tatoeba seed decomposer's job is to hand the raw string to the text decomposer, receive back the root text_composition hash, and attach metadata edges/junctions on top.

**Same content = same hash at every level of the AST.** "Hello world" in a Tatoeba row, in a WordNet example gloss, in a Wiktionary citation, in a user prompt, and in a model-generated output all collapse to ONE `text_composition` entity with ONE hash.

---

## Architecture

```
┌───────────────────────────────────────────────────────────────────┐
│  Decomposer threads (modality or seed; can run in parallel)       │
│    parse source → emit records via IRecordSink.EmitAsync          │
│    no batch boundary in the producer's API                        │
└──────────────────────────────┬────────────────────────────────────┘
                               │ EmitAsync (one record at a time)
                               ▼
┌───────────────────────────────────────────────────────────────────┐
│  StreamingIngestionPipeline  (Hartonomous.Engine.Ingestion)       │
│                                                                    │
│   8 bounded Channel<TRecord> (capacity 65,536 each, FullMode=Wait)│
│      Channel<EntityRecord>                                         │
│      Channel<EdgeRecord>                                           │
│      Channel<EdgeMemberRecord>                                     │
│      Channel<JunctionRecord>                                       │
│      Channel<PhysicalityRecord>                                    │
│      Channel<SequenceRecord>                                       │
│      Channel<EntitySignificanceRecord>                             │
│      Channel<EntityModelSourceRecord>                              │
│                              │                                     │
│   8 drain Tasks — one per kind, each holding a long-lived          │
│   NpgsqlBinaryImporter into the corresponding staging table.       │
│   Chunks commit at 4096 rows OR 250ms idle, whichever first.       │
└──────────────────────────────┬────────────────────────────────────┘
                               │ COPY (binary)
                               ▼
┌───────────────────────────────────────────────────────────────────┐
│  Persistent staging tables (substrate schema, migration 0019)     │
│    substrate.staging_entity                                        │
│    substrate.staging_edge                                          │
│    substrate.staging_edge_member                                   │
│    substrate.staging_junction (table_name discriminator)           │
│    substrate.staging_physicality                                   │
│    substrate.staging_sequence                                      │
│    substrate.staging_entity_significance                           │
│    substrate.staging_entity_model_source                           │
└──────────────────────────────┬────────────────────────────────────┘
                               │
                               │ ←  StagingFlushWorker (continuous)
                               │     calls substrate.drain_staging_*_chunk
                               │     in 4K-row chunks (65K during catch-up)
                               │     FOR UPDATE SKIP LOCKED — concurrent-safe
                               ▼
┌───────────────────────────────────────────────────────────────────┐
│  substrate.* (the actual substrate)                               │
│    substrate.entity, substrate.edge, substrate.edge_member,        │
│    substrate.physicality, substrate.sequence,                      │
│    substrate.entity_significance, substrate.edge_significance,     │
│    substrate.entity_model_source, substrate.entity_pos,            │
│    substrate.entity_lexname, substrate.entity_language, ...        │
└───────────────────────────────────────────────────────────────────┘
                               │
                               │ ←  BackgroundSignificancePrimer (continuous)
                               │     calls substrate.prime_unprimed_edges_chunk
                               │     iterates significance_context, primes
                               │     missing rows arena-by-arena
```

### Lifecycle

The orchestrator constructs ONE `StreamingIngestionPipeline`, ONE `StagingFlushWorker`, and ONE `BackgroundSignificancePrimer` per phase run (or per process lifetime). All decomposers in the phase share them.

```csharp
await using StreamingIngestionPipeline pipeline = new(conn, refDataReader, logger);
await using NpgsqlDataSource flushDs = NpgsqlDataSource.Create(conn);
await using StagingFlushWorker flushWorker = new(flushDs, flushLogger);
await flushWorker.StartAsync();
await using BackgroundSignificancePrimer primer = new(flushDs, primerLogger);
await primer.StartAsync();

// ... run decomposers, all sharing `pipeline` (cast to IRecordSink or IIngestionPipeline) ...

await pipeline.FlushAsync(ct);  // drain in-flight channels into staging
await primer.StopAsync();        // catch-up prime to empty
await flushWorker.StopAsync();   // catch-up drain to empty
```

---

## Producer surfaces

### `IRecordSink` (preferred, post-streaming-redesign)

```csharp
public interface IRecordSink
{
    ValueTask EmitAsync(IngestionRecord record, CancellationToken ct);
    ValueTask FlushAsync(CancellationToken ct);
}
```

Decomposers receive an `IRecordSink` and emit one `IngestionRecord` at a time. There is no batch boundary. Backpressure is automatic — when a channel fills, `EmitAsync` returns a `ValueTask` that completes when capacity becomes available.

### `IIngestionPipeline` (compatibility shim)

`StreamingIngestionPipeline` also implements `IIngestionPipeline`. Existing decomposers that build `IngestionBatch` keep working — the shim unfolds each batch into per-record `EmitAsync` calls. This is the migration ramp: every decomposer benefits from streaming immediately; per-decomposer migrations to `IRecordSink` are opt-in optimizations.

---

## Record types

Eight discriminated-union subtypes of `IngestionRecord` (`Hartonomous.Core.Ingestion.*`):

| Record type                  | Maps to                          |
|------------------------------|----------------------------------|
| `EntityRecord`               | `substrate.entity`               |
| `EdgeRecord`                 | `substrate.edge`                 |
| `EdgeMemberRecord`           | `substrate.edge_member`          |
| `JunctionRecord`             | `substrate.entity_pos` / `substrate.entity_lexname` / `substrate.entity_language` / `substrate.entity_morph_feature` / `substrate.model_architecture_class` / `substrate.tensor_tensor_role` / `substrate.pattern_deprel` (routed by `JunctionTable` discriminator) |
| `PhysicalityRecord`          | `substrate.physicality`          |
| `SequenceRecord`             | `substrate.sequence`             |
| `EntitySignificanceRecord`   | `substrate.entity_significance`  |
| `EntityModelSourceRecord`    | `substrate.entity_model_source`  |

`BaseDecomposer` exposes static helpers for emitting each kind: `EmitEntityAsync`, `EmitEdgeAsync` (computes `edge_hash` from role-ordered participant hashes), `EmitJunctionAsync`, `EmitPhysicalityAsync` (computes content hash from WKB), `EmitSequenceAsync`, `EmitEntitySignificanceAsync`, `EmitEntityModelSourceAsync`.

---

## Staging + drain

Migration `0019_persistent_staging` creates 8 staging tables in `substrate.*`. Each is a queue: producers COPY in, drainer drains in chunks. ctid-based draining (`SELECT ctid LIMIT N FOR UPDATE SKIP LOCKED → INSERT ... ON CONFLICT DO NOTHING → DELETE WHERE ctid IN (...)`) gives concurrent-flusher safety; multiple workers can run if needed.

`substrate.drain_staging_*_chunk(p_chunk_size INT)` returns the count of rows drained. Background worker loops calling each in turn, sleeps when all return 0.

Staging tables are NOT partitioned — they're queues. The drain functions do partition routing on the way to substrate. Per-partition `INSERT ... ON CONFLICT DO NOTHING` is the substrate-side operation; bulk-INSERT pressure stays bounded because chunks are small (4K rows, 64K during shutdown catch-up).

---

## Significance priming

`substrate.prime_unprimed_edges_chunk(p_arena_id INT, p_chunk_size INT)` finds N edges with no `substrate.edge_significance` row in a given arena, primes them with the compound-formula μ:

```
μ₀ = COALESCE(
       provenance_edge_authority.initial_mu,
       provenance.initial_mu × edge_type.semantic_weight × provenance.derivation_decay
     )
```

`BackgroundSignificancePrimer` iterates arenas read from `substrate.significance_context` (open-vocabulary — new arenas auto-pick-up via 30-second arena-list refresh), primes each in turn, sleeps on idle.

Critically: this is **NOT** inside any producer transaction. The synchronous `prime_edge_significance_for_staging` call inside the old per-batch path (which crashed PG with stack canary failures in `ExecInterpExpr` under bulk-INSERT pressure) is gone.

---

## Performance baselines

| Phase           | Old per-batch wall | Streaming wall | Speedup |
|-----------------|--------------------|----------------|---------|
| UCD/UCA         | 50.7s              | 20.7s          | 2.4x    |
| ISO 639         | 3.3s               | 2.1s           | 1.6x    |
| WordNet + OMW   | 414.8s             | 176.4s         | 2.4x    |
| Universal Deps  | crashed (SIGSEGV)  | (proof point — see UD task)  |  —      |

Baselines on a 14900KS / 48GB DDR5 / NVMe RAID0 host running PG18 + PostGIS 3.6 + hartonomous extension (icx-built libhartonomous, MKL-linked, AVX2 paths active).

The streaming pipeline's COPY commits amortize I/O cost: 11,026 commits for the WordNet+OMW run vs ~525 batches × 7 substrate tables = 3,675 transactions for the same data on the old pipeline (each old transaction was much heavier — staging table CREATE/DROP × 7 + flush function calls × 7 + prime call inside the batch).

---

## Banned patterns (post-streaming-redesign)

- **Per-batch transactions in the producer path.** The old `NpgsqlIngestionPipeline.SubmitBatchAsync` opened a transaction, did all 7 sub-phases of CREATE TEMP / COPY / flush, and committed. The streaming pipeline has zero per-batch transactions. COPY commits run on chunk-size or idle threshold inside the long-lived drain task.
- **Synchronous `SELECT substrate.prime_edge_significance_for_staging()` inside a producer transaction.** This was the crash site. Significance priming is now a separate background task on its own connection.
- **TEMP staging tables created and dropped per batch.** Staging is persistent (`substrate.staging_*`) and shared across all producers.
- **Decomposer-owned `Channel.CreateBounded` / `Parallel.ForEachAsync`.** The pipeline owns channels. Decomposers can use `Parallel.ForEachAsync` for *source-parsing parallelism* (e.g., UD's 270+ treebanks parsed in parallel), but they all push into the same shared `IRecordSink`.
- **`ComputeHash(string)` on user-visible text inside a seed decomposer.** Routes through the text core decomposer which produces the canonical text AST.
- **Cherry-picking a subset of `significance_context` arenas.** The primer iterates whatever arenas exist at refresh time. Code that hardcodes the 10 starter arena codes is wrong.
- **Pass-2 walks over accumulated hashes.** The streaming model produces records once, in source order; cross-batch identity is handled by content hashes, not by a second pass.

---

## Cross-references

- `src/Hartonomous.Core/Ingestion/IRecordSink.cs` — the producer surface
- `src/Hartonomous.Core/Ingestion/IngestionRecord.cs` (+ 8 subtype files) — record kinds
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` — the core pipeline
- `src/Hartonomous.Engine/Ingestion/StagingFlushWorker.cs` — background drain
- `src/Hartonomous.Engine/Ingestion/BackgroundSignificancePrimer.cs` — background priming
- `sql/migrations/0019_persistent_staging.up.sql` — staging tables + drain functions
- `sql/schema/functions/drain_staging_chunk.sql` — drain function definitions
- `sql/schema/functions/prime_unprimed_edges_chunk.sql` — significance primer
- `.claude/rules/00-hartonomous-core.md` § "Ingestion pipeline is centralized; decomposers are pure producers"
- `.claude/rules/45-anti-patterns.md` AP-2 (inline SQL in C#), AP-1 (arena cherry-picking)
