# Ingestion Pipeline

**Status**: 🚧 Architectural invariant documented; current implementation has pass-2 anti-patterns in seed decomposers (OMW, UCD, WordNet) that must be eliminated. See § *Invariant* and § *Anti-patterns* below.

The bridge between every decomposer (modality or seed) and the substrate. One centralized pipeline owns batching, partitioning, parallelization, threading, async, commit boundaries, hash→id resolution, and backpressure. Every decomposer is a pure record producer.

---

## Invariant

**The pipeline is invention-level infrastructure. Decomposers are adapters.**

There is exactly one ingestion pipeline. Every decomposer — text, image, audio, video, telemetry, chess games, DNA, medical data, safetensors models, UCD, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba — produces records for that pipeline and nothing else. No decomposer owns a transaction. No decomposer owns a channel. No decomposer calls `ResolveEntityIdsAsync` to stitch its own cross-batch joins. No decomposer runs a "pass 2" over accumulated hashes.

Two classes of decomposer, both producers, no architectural difference:
1. **Modality (core) decomposers** — ingest native content of a modality. Text, image, audio, video, telemetry, chess PGN, DNA FASTA, medical DICOM, safetensors weights, etc. These OWN the AST decomposition for their modality (e.g., the text decomposer owns codepoint → grapheme_cluster → morpheme → word_form → text_composition → paragraph → document).
2. **Seed decomposers** — ingest authoritative foundational lexicons so the substrate has structural grammar to reason against. UCD/UCA, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba. The database IS the model; seed decomposers seed its foundational grammar.

### Seed decomposers USE core decomposers — they do not bypass them

A Tatoeba sentence is NOT a flat `tatoeba_sentence` atom that carries the raw string. It is a full text AST produced by the TEXT core decomposer: codepoint → grapheme_cluster → morpheme → word_form → text_composition (Merkle) → paragraph → document. Each level is an entity with its own BLAKE3 content hash. The Tatoeba seed decomposer's job is to hand the raw string to the text decomposer, receive back the root text_composition hash, and attach metadata edges/junctions on top (provenance = tatoeba, `entity_language` = eng, `translation_link` to another sentence, `has_contributor`, etc.).

**Same content = same hash at every level of the AST.** "Hello world" in a Tatoeba row, in a WordNet example gloss, in a Wiktionary citation, in a user prompt, and in a model-generated output all collapse to ONE `text_composition` entity with ONE hash. The per-source provenance/edges/junctions diverge; the content atom never duplicates. This is the invention — content addressing at every tier of the AST, shared across every decomposer that encounters that content.

This principle applies everywhere text lives:
- **WordNet glosses and examples** — full text AST, not opaque text_composition atoms. "a tool for gathering leaves" decomposes into its word_forms, morphemes, graphemes, codepoints. Every one is shared with any other text containing those substrings.
- **Wiktionary** definitions, etymologies, pronunciations (IPA is text), hyphenation annotations — text AST.
- **UD sentences** — text AST via the text decomposer; UD overlays syntactic dependency edges on top of the word_forms the text decomposer already produced.
- **Safetensors** model config JSON string values, tokenizer vocab entries, architecture metadata — text AST.
- **Any modality carrying embedded text** — image captions, audio transcripts, video subtitles, chess PGN comments, medical report narrative — text AST.

And symmetrically for other modalities when they appear embedded in text: an image URL in a Wiktionary entry, an audio clip referenced from a Tatoeba row, a tensor referenced from a model card — the seed/metadata decomposer hands the bytes to the core decomposer for that modality and references the resulting content hash.

### Phase ordering consequence

UCD/UCA must run first — it seeds codepoint atoms. Immediately after, the TEXT core decomposer service must be available (via DI) to every subsequent decomposer. WordNet, UD, Wiktionary, Tatoeba, Safetensors, and every modality decomposer route their embedded text through it. No decomposer in the system calls `ComputeHash(string)` directly on a user-visible string and emits it as an atomic entity — that bypasses the AST and breaks same-content-same-hash.

---

## Architecture

```
Decomposer (streaming producer — modality or seed, no distinction)
  → creates IIngestionBatch
  → emits records: entity (hash), edge (type + member hashes/handles),
                   junction, physicality, sequence, significance
  → calls pipeline.SubmitBatchAsync(batch) when batch thresholds are reached
  → SINGLE PASS over the source — no pass-2, no cross-batch accumulation

Pipeline (NpgsqlIngestionPipeline — owns everything non-record)
  → batching, flush thresholds, bounded-channel parallelism
  → per-batch transaction
  → UpsertEntitiesAsync (hash → id)
  → resolves cross-batch hashes in edges/junctions via
    SELECT id FROM substrate.entity WHERE hash = ANY($1)
  → remaps EntityHandles + resolved hashes to real entity_ids
  → calls stored procedures in FK order
  → commits transaction
  → reports stats + progress
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

## Anti-patterns (current code violations — to be eliminated)

1. **Pass-1 (atoms) / Pass-2 (connective tissue) inside a decomposer.** A decomposer that flushes entity batches, then calls `pipeline.ResolveEntityIdsAsync(allHashes)` against the entire phase's hash set, then iterates a second time to emit edges and junctions by integer id, is doing the pipeline's job badly. Symptoms: `List<(byte[] LemmaHash, byte[] SynsetHash, double TrustMu)> alignments = new(3_000_000)` (OMW), two-pass loops over `allCodepoints` (UCD), large `glossEntries`/`exampleEntries` lists held across the phase (WordNet). Memory grows linearly in the phase input, not in the batch. Replace by emitting edges at the point of production via hash-valued `EdgeMemberSpec` that the pipeline resolves at batch-commit time (`SELECT id FROM substrate.entity WHERE hash = ANY($1)` for any hashes not present in the current batch).

2. **Decomposer-owned parallelism.** UD and Wiktionary each implement their own `Channel.CreateBounded` producer/consumer over `Parallel.ForEachAsync`. That logic belongs on the pipeline, applied uniformly across every decomposer. Decomposers should be single-threaded streaming producers; the pipeline distributes work.

3. **Seed decomposers bypassing core decomposers.** Tatoeba emitting `tatoeba_sentence` entities as atoms whose hash comes from the raw string directly. WordNet emitting gloss and example strings as opaque `text_composition` entities without running them through the text decomposer. Wiktionary storing etymology text and IPA pronunciation as flat strings. Every one of these routes its text content around the text-modality AST, producing `text_composition` hashes that will NOT match the same content appearing in a user prompt or another seed source. This defeats the invention.

4. **Decomposer-side `ComputeHash(string)` on user-visible text.** Any time a decomposer takes a multi-character string destined for a `text_composition`-tier entity and hashes it directly, it is bypassing the AST. The only callers that may hash raw strings as atoms are the core decomposers of the matching modality (text decomposer hashes graphemes/codepoints; image decomposer hashes pixel regions; audio decomposer hashes audio chunks), and only at the atom tier.

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
