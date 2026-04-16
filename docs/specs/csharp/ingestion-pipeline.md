# Ingestion Pipeline

**Status**: ✅ Complete

The bridge between C# decomposer output and PostgreSQL stored procedures. Batching, transactions, call sequence, and connection management.

---

## Architecture

```
Decomposer
  → creates IIngestionBatch (in-memory buffer)
  → calls pipeline.SubmitBatchAsync(batch)

Pipeline (NpgsqlIngestionPipeline)
  → opens transaction
  → resolves entity hashes → IDs (SELECT + batch_upsert_entities)
  → remaps EntityHandles to real entity_ids
  → calls stored procedures in FK order
  → commits transaction
  → reports stats
```

---

## Batching

### What Is a Batch

A batch is an `IIngestionBatch` — an in-memory buffer of pending operations assembled by a decomposer during parsing. A batch contains:

- Entities (hash + type code)
- Edges (type code + provenance + members referencing EntityHandles)
- Junctions (junction table + entity handle + reference ID + optional mu)
- Physicalities (entity handle + type code + WKB geometry)
- Sequences (parent handle + child handle + position + count)
- Significance initializations (entity handle + context code + initial mu)

### Batch Size

Configurable via `DecomposerConfig.BatchSize`. Default: **10,000 entities** per batch.

Edges, junctions, physicalities, and sequences scale with entity count — a batch of 10,000 entities might contain 50,000 edges, 30,000 junctions, 10,000 physicalities, etc. The entity count is the control knob.

Upper bound: **100,000 entities** per batch. Beyond this, the single-transaction approach risks long lock hold times and large WAL generation.

### Assembly Pattern

```csharp
// In a decomposer's DecomposeCoreAsync:
var batch = pipeline.CreateBatch();

foreach (var entry in ParseSourceFile(path))
{
    var handle = batch.AddEntity(ComputeHash(entry.Content), entry.TypeCode);

    foreach (var edge in entry.Edges)
        batch.AddEdge(edge.TypeCode, ProvenanceCode, edge.Members);

    foreach (var junction in entry.Junctions)
        batch.AddJunction(junction.Table, handle, junction.ReferenceId, junction.Mu);

    if (batch.EntityCount >= BatchSize)
    {
        await SubmitAndReportAsync(pipeline, reporter, batch, snapshot, ct);
        batch = pipeline.CreateBatch();
    }
}

// Submit the final partial batch
if (batch.EntityCount > 0)
    await SubmitAndReportAsync(pipeline, reporter, batch, snapshot, ct);
```

---

## Transaction Boundaries

**One transaction per batch.** The entire batch submits atomically.

```
BEGIN TRANSACTION
  → batch_upsert_entities(...) → returns entity_ids
  → batch_create_edges(...)    → creates edges + edge_members atomically
  → populate_junction(...)     → per junction table
  → create_physicality(...)    → per physicality entry
  → create_sequence(...)       → per sequence entry
  → initialize_significance(...)
COMMIT
```

If any step fails, the entire transaction rolls back. No partial batches in the database. The decomposer receives the exception and propagates it — the phase runner halts.

**No savepoints.** A batch is the atomic unit. If one entity in a 10,000-entity batch has a corrupt hash, the entire batch fails. The operator fixes the source data and re-runs the decomposer. The decomposer is idempotent (entity upsert on hash + ON CONFLICT DO NOTHING) so re-running is safe.

---

## Call Sequence

The order of stored procedure calls within a transaction is FK-constrained:

### Step 1: Entity Upsert

```csharp
// Resolve which entities already exist
var existingHashes = new HashSet<byte[]>(ByteArrayComparer.Instance);
var existing = await ResolveEntityIdsAsync(batch.AllHashes, ct);
foreach (var kvp in existing)
    existingHashes.Add(kvp.Key);

// Upsert new entities only
var newEntities = batch.Entities
    .Where(e => !existingHashes.Contains(e.Hash))
    .ToList();

if (newEntities.Any())
{
    // CALL substrate.batch_upsert_entities(
    //   p_hashes := ARRAY[...],
    //   p_entity_type_codes := ARRAY[...]
    // )
    // Uses unnest + LEFT JOIN pattern from stored-procedures.md
}

// Re-resolve all entity IDs (both existing and newly created)
var allIds = await ResolveEntityIdsAsync(batch.AllHashes, ct);
// Remap all EntityHandles → real entity_ids
batch.RemapHandles(allIds);
```

### Step 2: Edge Creation

```csharp
// CALL substrate.batch_create_edges(
//   p_hashes := ARRAY[...],
//   p_edge_type_codes := ARRAY[...],
//   p_provenance_codes := ARRAY[...],
//   p_member_entity_ids := ARRAY[ARRAY[...]],
//   p_member_role_codes := ARRAY[ARRAY[...]],
//   p_member_positions := ARRAY[ARRAY[...]]
// )
// Atomically creates edges + edge_members per stored-procedures.md
```

### Step 3: Junction Population

```csharp
// Per junction table, one call each:
// CALL substrate.populate_junction('entity_pos', p_entity_ids, p_reference_ids, p_mus)
// CALL substrate.populate_junction('entity_sense', p_entity_ids, p_reference_ids, p_mus)
// etc.
// Whitelist-validated dynamic SQL per stored-procedures.md
```

### Step 4: Physicality Creation

```csharp
// Per physicality entry:
// CALL substrate.create_physicality(p_entity_id, p_physicality_type_code, p_geom)
// Geometry passed as WKB (Well-Known Binary) for efficient transfer
```

### Step 5: Sequence Creation

```csharp
// Bulk INSERT into substrate.sequence using unnest:
// INSERT INTO substrate.sequence (parent_id, child_id, position, count)
// SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::int[], $4::int[])
```

### Step 6: Significance Initialization

```csharp
// CALL substrate.initialize_significance(p_entity_id, p_context_type_code, p_initial_mu)
// Initial mu from provenance trust prior
```

---

## Connection Management

### Npgsql Data Source

```csharp
public sealed class NpgsqlIngestionPipeline : IIngestionPipeline
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlIngestionPipeline(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNetTopologySuite();  // PostGIS geometry support
        _dataSource = builder.Build();
    }

    public async ValueTask DisposeAsync()
        => await _dataSource.DisposeAsync();
}
```

**Connection pooling**: Npgsql's built-in pool. Default size: 10. One `NpgsqlDataSource` per pipeline instance (one per decomposer run).

**Connection string**: `Host=localhost;Port=5432;Database=hartonomous;Username=postgres;Password=postgres;Maximum Pool Size=10;Timeout=30`.

No connection-per-batch. The pool manages connections internally. `SubmitBatchAsync` acquires a connection from the pool, runs the transaction, and releases it.

---

## Bulk Mode vs Incremental Mode

### Phase 1 Bulk Mode (Seed Ingestion)

- Deferred indexes are absent (per indexing.md Phase 2).
- Batch size raised to 50,000-100,000 entities.
- Stored procedures are called (not COPY). Reason: entity upsert deduplication requires the hash → id lookup pattern, which COPY cannot do.
- After all decomposers complete, deferred indexes are created with `CREATE INDEX CONCURRENTLY` (Phase 3 of indexing.md).
- Post-index: `VACUUM ANALYZE` all tables.

### Phase 2+ Incremental Mode (Analysis Passes, User Content)

- All indexes present.
- Batch size at default 10,000.
- Same stored procedure call sequence.
- No special handling — the indexes exist, so insertion auto-maintains them.

The pipeline does not know which mode it is in. The difference is purely whether indexes exist yet. The phase runner controls when indexes are created.

---

## EntityHandle Remapping

`EntityHandle` is a batch-local reference. During batch assembly, the decomposer doesn't know real entity IDs — entities haven't been inserted yet. After Step 1 (entity upsert + resolve), the pipeline remaps all handles:

```csharp
internal void RemapHandles(IReadOnlyDictionary<byte[], long> hashToId)
{
    // For each EntityHandle in edges, junctions, physicalities, sequences:
    // look up the hash from the entity at that batch index,
    // resolve to real entity_id from hashToId dictionary
}
```

This is why edges, junctions, physicalities, and sequences reference `EntityHandle` during assembly but use real `long` entity IDs during submission.

---

## Pipeline Stats

```csharp
public sealed class PipelineStats
{
    public long EntitiesUpserted { get; internal set; }
    public long EntitiesSkippedDuplicate { get; internal set; }
    public long EdgesCreated { get; internal set; }
    public long JunctionsPopulated { get; internal set; }
    public long PhysicalitiesCreated { get; internal set; }
    public long SequencesCreated { get; internal set; }
    public long SignificanceInitialized { get; internal set; }
    public long BatchesSubmitted { get; internal set; }
    public TimeSpan TotalSubmitTime { get; internal set; }

    public double EntitiesPerSecond =>
        TotalSubmitTime.TotalSeconds > 0
            ? EntitiesUpserted / TotalSubmitTime.TotalSeconds
            : 0;
}
```

Updated by `SubmitBatchAsync` after each successful batch. Read by progress reporters and the phase runner for monitoring.
