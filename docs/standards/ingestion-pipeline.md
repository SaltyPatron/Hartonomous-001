# The Unified Ingestion Pipeline

There is ONE ingestion pipeline. Not one for seeding, one for runtime, one for bulk, one for single-entity. One pipeline that handles all of it.

## Why One Pipeline

Every decomposer — seed and runtime — produces the same substrate primitives: entities, edges, edge members, junction entries, physicalities, sequences, significance entries. The database schema is the same regardless of whether the data comes from WordNet at seed time or a user's uploaded document at runtime. One schema, one set of stored procedures, one pipeline that calls them.

If there were separate pipelines, they would inevitably drift: one would handle deduplication differently, another would skip junction population, a third would batch differently. The substrate's integrity depends on every piece of data taking the exact same path into the database.

## Pipeline Responsibilities

The pipeline owns ALL database interaction for writes. No decomposer, no analysis pass, no recomposer, no API endpoint writes directly to the database. Everything goes through `IIngestionPipeline`.

| Responsibility | What It Does |
|---------------|-------------|
| **Entity upsert** | BLAKE3 hash → check existence → insert or return existing ID. One procedure. |
| **Edge creation** | Hash → insert edge → insert edge members. One procedure. |
| **Junction population** | Entity + classification → insert into correct junction table with significance. One procedure per junction table, or one polymorphic procedure. |
| **Physicality creation** | Entity + geometry + type → insert physicality row. GiST index maintained automatically by PostgreSQL. |
| **Sequence creation** | Parent + children in order → insert sequence rows with RLE. One procedure. |
| **Significance initialization** | Entity or edge + arena + trust prior → insert initial Glicko-2 state. One procedure. |
| **Monitoring** | Every batch reports progress to `monitor.ingestion_progress`. Automatic, not opt-in. |

## Modal Operation: Single, Batch, and Bulk

The pipeline adapts its strategy based on volume. Same interface, same contract, different execution path internally.

```
┌──────────────────────────────────────────────────┐
│                 IIngestionPipeline                │
│                                                  │
│  SubmitAsync(IngestionUnit unit, ct)             │
│  SubmitBatchAsync(IngestionUnit[] units, ct)     │
│  BeginBulkAsync(ct) → IBulkSession              │
│                                                  │
│  All three produce the same substrate state.     │
│  All three call the same stored procedures.      │
│  All three report to monitor schema.             │
└──────────────────────────────────────────────────┘
```

| Mode | When | Strategy |
|------|------|----------|
| **Single** | Runtime user content, API-driven ingestion. One entity at a time. | Individual procedure calls within a transaction. Optimized for latency. |
| **Batch** | Analysis passes, moderate-volume decomposers. Hundreds to thousands of units. | Collected in memory, submitted as one transaction with parameterized procedure calls. |
| **Bulk** | Seed ingestion. Millions of rows. | Uses PostgreSQL `COPY` into staging tables, then a merge procedure moves staged data into live tables. Indexes can be dropped and rebuilt around the COPY for maximum throughput. |

The decomposer does not choose the strategy. The pipeline chooses based on the volume and the ingestion mode (seed vs. runtime). The decomposer calls `SubmitAsync` or `SubmitBatchAsync` — the pipeline decides whether to accumulate into a larger batch, stream via COPY, or execute immediately.

## IngestionUnit: The Universal Work Item

Every decomposer produces `IngestionUnit` records. One unit = one atomic piece of substrate state to create.

```csharp
public abstract record IngestionUnit;

public sealed record EntityUnit(
    byte[] Hash,
    int EntityTypeId,
    int? ProvenanceId) : IngestionUnit;  // null = shared substrate (seed), populated = user content

public sealed record EdgeUnit(
    byte[] Hash,
    int EdgeTypeId,
    int? ProvenanceId,                   // null = shared substrate (seed), populated = user content
    IReadOnlyList<EdgeMemberUnit> Members) : IngestionUnit;

public sealed record EdgeMemberUnit(
    byte[] EntityHash,  // resolved to entity_id by the pipeline
    int RoleId,
    short Position);

public sealed record PhysicalityUnit(
    byte[] EntityHash,
    int PhysicalityTypeId,
    Geometry Geom) : IngestionUnit;  // NetTopologySuite Geometry

public sealed record SequenceUnit(
    byte[] ParentHash,
    byte[] ChildHash,
    int Position,
    int Count) : IngestionUnit;

public sealed record JunctionUnit(
    byte[] EntityHash,
    string JunctionTable,   // "entity_pos", "entity_sense", etc.
    int ClassificationId,   // FK to the reference table
    double? InitialMu) : IngestionUnit;

public sealed record SignificanceUnit(
    byte[] TargetHash,      // entity or edge hash
    bool IsEdge,
    int ContextTypeId,
    double Mu,
    double Sigma,
    double Volatility) : IngestionUnit;
```

The decomposer builds these. The pipeline consumes them. The decomposer never knows what SQL runs. The pipeline never knows what source data looks like.

## Provenance: Tenant/User Identity

All substrate content falls into two categories:

- **Shared substrate** (seed data, model-derived knowledge): `ProvenanceId = null`. No tenant owns it. Every tenant reads it. Created during seed ingestion by admin. This is WordNet synsets, UCD codepoints, UD syntactic structures, OMW cross-lingual alignments — the shared knowledge graph.
- **User content** (prompts, documents, images, audio, video, telemetry): `ProvenanceId` references a row in `substrate.provenance(provenance_id, tenant_id, user_id, source, created_at)`. This content is scoped — one tenant's data is invisible to another tenant's queries.

The pipeline manages provenance lifecycle, not the decomposer:

1. **Seed ingestion**: pipeline sets `ProvenanceId = null` on all units. No provenance record created.
2. **User content ingestion**: pipeline creates ONE provenance record at session start (`INSERT INTO substrate.provenance (tenant_id, user_id, source) VALUES (...) RETURNING provenance_id`), then stamps that ID onto every `EntityUnit` and `EdgeUnit` produced during the session.
3. **The decomposer never sets ProvenanceId**. It yields units with `ProvenanceId = null`. The pipeline overrides it from the session context.

User data is persistent. It does not expire, it is not session-scoped in the traditional sense. A user's entire ingestion history — every prompt, every document, every conversation — forms one continuous, ever-growing substrate graph scoped by `(tenant_id, user_id)`. The system never forgets unless explicitly told to delete. This is the infinite context window: prior prompts are not "recalled" — they are already in the graph, always addressable, always traversable.

## Concurrency

The pipeline is thread-safe. Multiple decomposers (or multiple instances of the same decomposer processing different files) can submit concurrently. The pipeline manages connection pooling, transaction isolation, and deadlock retry internally.

### C#-Side Deduplication (Primary Strategy)

The pipeline does NOT rely on database `ON CONFLICT` as its primary deduplication mechanism. Heavy dedup logic lives in C#:

1. **Collect hashes**: the pipeline accumulates hashes from the current batch of `IngestionUnit` records.
2. **Batch existence check**: one round-trip — `SELECT hash, id FROM substrate.entity WHERE hash = ANY($1)`. Returns the subset that already exist with their IDs.
3. **Partition**: units whose hashes were found are "existing" (skip insert, use returned ID for edge resolution). Units whose hashes were NOT found are "new" (insert).
4. **Route by state**:
   - **New entities**: plain `INSERT` (no `ON CONFLICT` needed — we already know they don't exist).
   - **Existing entities**: skip insert, map hash → ID from the lookup result. Used for edge member resolution.
   - **Edges**: same pattern — batch-check edge hashes, insert only new ones.

This approach is faster for large batches because:
- **One round-trip** checks N hashes vs N individual `ON CONFLICT` checks.
- The pipeline knows **upfront** what work needs to be done and can size transactions, allocate buffers, and report progress accurately.
- Under bulk mode, the pipeline can skip the existence check entirely for hash ranges it knows are novel (e.g., first-time seed ingestion of a new decomposer's output).

### Concurrent Safety Net

`ON CONFLICT (hash) DO NOTHING` remains on the `UNIQUE` constraint as a **safety net** for concurrent writers only. If two pipeline instances both check for hash X, both see it missing, both try to insert — one succeeds, one hits the constraint harmlessly. This is a rare race condition, not the normal dedup path.

- **Significance updates** use `READ COMMITTED` isolation — concurrent arena updates from different sessions serialize on row-level locks, MVCC handles read consistency.
- **Deadlock retry** is bounded and automatic inside the pipeline. The caller never sees a deadlock — the pipeline retries the transaction (bounded, with backoff) or fails loud.

## Index and GiST Exploitation

The pipeline is aware of the database's physical layout and exploits it:

- **Deduplication lookups** hit `ix_entity_hash` (B-tree UNIQUE) — O(1) per entity. The pipeline batches these lookups when possible: send N hashes, get back the subset that already exist, insert only the new ones.
- **GiST inserts** for physicality are naturally incremental — PostGIS maintains the GiST index on every insert. During bulk mode, the pipeline drops and rebuilds GiST indexes around the COPY for dramatically faster loading.
- **Junction table inserts** hit composite B-tree indexes — the pipeline batches these to minimize index page splits.
- **Sequence inserts** are ordered by `(parent_id, position)` — the pipeline sorts them before submission so the B-tree index grows in order, not randomly.

## Decomposer Relationship

Decomposers are data producers. The pipeline is the data consumer. The boundary is the `IngestionUnit` type.

```
┌─────────────┐     IngestionUnit[]     ┌────────────────────┐     CALL/COPY     ┌──────────────┐
│  Decomposer │ ──────────────────────→ │ IngestionPipeline  │ ────────────────→ │  PostgreSQL  │
│  (C#)       │                         │ (C#)               │                   │  (substrate) │
│             │  Knows: source format   │                    │  Knows: SQL API   │              │
│             │  Produces: units        │  Knows: batching,  │  Calls: procs,    │  Knows:      │
│             │  Does NOT know: SQL     │  COPY, concurrency │  COPY, functions  │  everything  │
└─────────────┘                         └────────────────────┘                   └──────────────┘
```

Every decomposer inherits from `BaseDecomposer` which provides:
- `IIngestionPipeline` (injected, never constructed)
- Helper methods that produce `IngestionUnit` records from parsed source data
- Progress reporting (automatic, via pipeline's monitor integration)

The decomposer's job is to parse its source format and yield `IngestionUnit` values. Period. It does not batch, it does not manage connections, it does not retry, it does not report progress directly. The pipeline does all of that.
