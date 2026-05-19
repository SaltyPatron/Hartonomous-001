# Recipe 00: The Vertical Slice — Input to Output, End to End

The canonical end-to-end walkthrough. Read this once. It shows how every piece of the substrate connects, from "user has a text file" to "user has an inference answer." Every other recipe is a localized version of one step in this slice.

---

## Intent

Demonstrate the full flow of one piece of content through the substrate:
1. Input file → 2. Decomposer → 3. Ingestion pipeline → 4. Substrate state → 5. Inference query → 6. Recomposed output.

This is what every operation in Hartonomous reduces to. If your work doesn't touch one of these steps, it isn't part of the substrate; if it touches multiple, it should still respect the boundaries between them.

---

## Prerequisites

- Repo cloned, dependencies installed via the Linux entrypoints documented under `scripts/`.
- Database bootstrapped from the generated `hartonomous` PostgreSQL extension: `scripts/hart db bootstrap`.
- Native and .NET projects built: `scripts/hart build native` and `scripts/hart build dotnet`.
- Seed data loaded for the slice being exercised: `scripts/hart seed all` or the relevant phase command.

If any of those fail, fix them before continuing. The vertical slice assumes a healthy substrate.

---

## The slice

### Step 1: Input arrives

A file appears on disk. For this walkthrough, a small text file:

```
input/example.txt
"The brown dog ran across the yard."
```

The file is content. It is not yet anything to the substrate.

### Step 2: A decomposer parses it

The text decomposer (`src/Hartonomous.Decomposers/Text/TextDecomposer.cs`) is invoked via:

```bash
scripts/hart phase run TextDecomp --path input/example.txt
```

Internally, the CLI command in `src/Hartonomous.Cli/Commands/IngestTextCommand.cs` resolves dependencies and calls:

```csharp
await textDecomposer.DecomposeAsync(pipeline, reporter, ct);
```

The decomposer does NOT own the database connection. It does NOT manage transactions. It does NOT batch. It is a streaming producer of *records*:

- One `EntitySpec` per codepoint in the file (BLAKE3 hash of the codepoint integer)
- One `EntitySpec` per grapheme cluster (Merkle hash of constituent codepoint hashes)
- One `EntitySpec` per word_form (Merkle hash of constituent grapheme hashes)
- One `EntitySpec` for the whole text_composition (Merkle hash of word-form hashes)
- `EdgeSpec` records connecting them via typed substrate edges such as `has_lemma`.
- `PhysicalitySpec` records carrying GeometryZM physicality: entity/body shape where needed, and mantissa-packed composition trajectories whose vertex stream stores child hash prefixes plus ordinal/RLE metadata.

Every record is content-addressed. Every record is deterministic.

### Step 3: The ingestion pipeline batches and commits

`Hartonomous.Engine.Ingestion.StreamingIngestionPipeline` (the ONE pipeline for all decomposers) receives records via `IRecordSink.EmitAsync`. It:

1. Each record kind has a bounded `Channel<T>` and a long-lived drain task that COPYs into a session-scoped `pg_temp` inflight table, then `INSERT INTO substrate.X SELECT ... FROM pg_temp.X_inflight ON CONFLICT DO NOTHING` per chunk.
2. Routes inserts to the correct partition by hash bucket / `edge_type_id` / `physicality_type_id`. The semantic identity of `substrate.entity` is still the BLAKE3 hash; the physical primary key includes `partition_bucket` only for PostgreSQL partitioning.
3. Producer-side dedup via per-channel `HashSet<Hash32>`; cross-session duplicates land in COPY but are discarded by `ON CONFLICT DO NOTHING`.
4. No per-batch transaction in the producer path. Backpressure: `EmitAsync` awaits naturally when a channel is full.
5. `DrainPendingAsync` waits for channels to quiesce, then runs edge trajectory population and per-arena significance priming before returning. Phases are runner orchestration, not substrate consistency boundaries.

The decomposer never sees entity hashes resolved through any registry. It never opens a connection. It emits records and the channels handle the rest.

### Step 4: Substrate state exists

After the commit, the database holds:

- New rows in `substrate.entity` (or no-ops for hashes that already existed), with structural classifications in `substrate.entity_classification`.
- New rows in `substrate.physicality` for entities/compositions that have geometry. Ordered children are recorded in the composition GeometryZM vertex stream via mantissa packing; there is no sidecar ordering table.
- New rows in `substrate.edge` for `has_lemma`, dependency relations (if UD pass ran), etc.
- New rows in `substrate.edge_member` for edge participants

If you were to delete the file from disk and re-run the decomposer, the second run would produce zero new rows — the BLAKE3 hashes match, every `INSERT ... ON CONFLICT DO NOTHING` is a no-op. **This is Law #6**: same input, same substrate state.

### Step 5: An inference query

A practitioner asks: "What entities does the substrate know that are related to 'dog' in this context?"

The CLI command in `src/Hartonomous.Cli/Commands/InferCommand.cs`:

```bash
scripts/hart phase run InferenceEngine --query "the brown dog ran across the yard"
```

Internally:

```csharp
var result = await inferenceEngine.InferAsync(
    new InferenceQuery { Text = "the brown dog ran across the yard", MaxDepth = 6, MaxResults = 20 },
    ct);
```

`SubstrateInferenceEngine` (`src/Hartonomous.Engine/Inference/SubstrateInferenceEngine.cs`) does:

1. Decomposes the query text into seed entities (using the same `TextDecomposer`, so query and ingested content share hashes).
2. Resolves seed `entity_id`s via hash lookup.
3. Calls `ITraversal.TraverseAsync` — the A\* traversal over typed edges, with Glicko-2 ratings as the cost heuristic.
4. Selects top-k paths.
5. Gathers entity metadata for the path endpoints.
6. Returns `InferenceResult { SeedEntityIds, Paths, Entities, NodesVisited, Elapsed }`.

The traversal is `O(K log N)` where K is the path budget. No model inference. No matrix multiplication. No GPU.

### Step 6: A recomposer renders output

The result is structured. To produce human-readable output:

```csharp
var text = await textRecomposer.RecomposeAsync(result.Paths, ct);
```

`TextRecomposer` (`src/Hartonomous.Recomposers/TextRecomposer.cs`):

1. Receives traversal paths.
2. Walks each path's substrate entities.
3. Reconstructs text deterministically from substrate state (no learned generation).
4. Returns the recomposed string.

The output is the substrate's truthful answer about what it knows of the query, in the form of recomposed text. It is reproducible: same query, same substrate state, same output. Byte-identical.

---

## What each component IS and IS NOT

### Decomposer

| IS | IS NOT |
|---|---|
| A streaming producer of typed records | A consumer of substrate state |
| Deterministic | Allowed to use ML for "interpretation" |
| Owner of the AST decomposition for its modality | Owner of database connections |
| Caller of `IIngestionPipeline.SubmitBatchAsync(batch)` | Caller of `NpgsqlConnection` directly |
| A user of `BaseDecomposer.ComputeHash`, `ComputeMerkleHash`, `ComputeEdgeHash` | A user of `Channel.CreateBounded` or `Parallel.ForEachAsync` |

### Ingestion pipeline

| IS | IS NOT |
|---|---|
| The single owner of batching, parallelism, transactions | Per-decomposer; there is exactly one |
| Owner of hash → entity_id resolution | A producer of records |
| Routes by partition (entity/edge/physicality type) | Aware of decomposer-specific source formats |
| Set-based bulk INSERT user | Per-row INSERT user |

### Substrate state

| IS | IS NOT |
|---|---|
| Content-addressed, deterministic | A learned representation |
| The truth of what has been ingested | A cache or snapshot of computation |
| Modified only via the ingestion pipeline | Written ad-hoc by analyzers, recomposers, or inference |
| Indexed by GiST / B-tree / BRIN per partition | A flat heap |

### Inference engine

| IS | IS NOT |
|---|---|
| Glicko-weighted A\* over typed edges, O(K log N) | A model forward pass, O(N²·d) |
| A reader of substrate state | A writer of substrate state (except for session-scoped Glicko updates) |
| A returner of named paths with rated edges | A returner of generated text |
| Optional 4D NN sidecar for similarity-class queries | A vector-DB-style retriever as primary path |

### Recomposer

| IS | IS NOT |
|---|---|
| A deterministic reconstructor of output from substrate state | A generative model |
| Per-modality (text, image, audio, video, safetensors) | A general-purpose decoder |
| A consumer of `TraversalPath` / `EntityHandle` | A modifier of substrate state |

---

## Verification

The whole slice runs in:

```pwsh
pwsh scripts/test/Integration.ps1 -Filter VerticalSlice
```

This test:
1. Resets the DB.
2. Runs all seed phases.
3. Ingests `tests/Hartonomous.Integration.Tests/Fixtures/example.txt`.
4. Issues a known query.
5. Asserts the inference result matches a recorded expected output (byte-identical).
6. Asserts a re-run produces identical substrate state.

If this passes, the slice works. If anything in the repo breaks the slice, this test fails first.

---

## Where to go from here

Each step above is one or more recipes:

- **Step 2 (decomposer)** → recipe `08-add-decomposer.md`
- **Step 3 (pipeline)** → covered by the pipeline contract; you don't add pipelines, you add submitters
- **Step 4 (substrate)** → schema changes via recipes `02-add-entity-type.md`, `03-add-edge-type.md`, `04-add-physicality-type.md`, `05-add-junction-table.md`, `13-add-migration.md`
- **Step 5 (inference)** → modify `SubstrateInferenceEngine` per recipe `19-add-phase.md` for new phases; for traversal extensions, see `specs/engine/inference.md`
- **Step 6 (recomposer)** → recipe `10-add-recomposer.md`

If you don't know which step your task touches, you don't yet have a clear task. Re-scope before writing code.
