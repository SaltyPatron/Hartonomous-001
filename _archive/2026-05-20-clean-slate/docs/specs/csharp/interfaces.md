# Core Interfaces

**Status**: ✅ Complete

All core interfaces that define the system's contracts. Full method signatures, generic constraints, lifecycle semantics, and error contracts.

---

## IDecomposer

Contract for all decomposers — seed (WordNet, UD, UCD, etc.) and runtime (text, image, audio, video).

```csharp
public interface IDecomposer : IAsyncDisposable
{
    /// <summary>
    /// Unique code matching the provenance table. E.g., "princeton_wordnet", "universaldependencies".
    /// </summary>
    string ProvenanceCode { get; }

    /// <summary>
    /// Human-readable name for logging and progress reporting.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Phases this decomposer runs in, in execution order.
    /// Most decomposers run in exactly one phase.
    /// </summary>
    IReadOnlyList<Phase> Phases { get; }

    /// <summary>
    /// Validate that source data exists and is readable before ingestion begins.
    /// Throws SourceValidationException if sources are missing or corrupt.
    /// </summary>
    Task ValidateSourceAsync(CancellationToken ct);

    /// <summary>
    /// Run decomposition. Creates entities, edges, junctions, physicalities, sequences
    /// via the provided pipeline. Reports progress via the provided reporter.
    /// 
    /// Must not catch exceptions. Failures propagate to the phase runner, which halts.
    /// </summary>
    Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);
}
```

**Lifecycle**: Constructed by DI → `ValidateSourceAsync` → `DecomposeAsync` → `DisposeAsync`.

**Error contract**: Throws `SourceValidationException` from validate. Any other exception from decompose halts the phase. No catching, no swallowing, no fallback.

**Thread safety**: Not thread-safe. One instance per decomposer per phase run. The phase runner creates and disposes instances.

**Implementors**: `UcdUcaDecomposer`, `Iso639Decomposer`, `WordNetDecomposer`, `OmwDecomposer`, `UdDecomposer`, `WiktionaryDecomposer`, `TatoebaDecomposer`, `SafetensorsDecomposer`, `TextDecomposer`, `ImageDecomposer`, `AudioDecomposer`, `VideoDecomposer`.

---

## IAnalysisPass

Contract for all analysis passes (37+ across 4 modalities). Passes consume ingested entities and produce new edges, physicalities, or significance entries.

```csharp
public interface IAnalysisPass
{
    /// <summary>
    /// Unique identifier for this pass. Used for dependency resolution and checkpoint tracking.
    /// E.g., "text.morphological_analysis", "audio.spectral_analysis".
    /// </summary>
    string PassId { get; }

    /// <summary>
    /// Which modality this pass belongs to.
    /// </summary>
    Modality Modality { get; }

    /// <summary>
    /// Pass IDs that must complete before this pass can run.
    /// Empty if no dependencies.
    /// </summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>
    /// Entity type codes this pass consumes. Used to query input entities.
    /// </summary>
    IReadOnlyList<string> InputEntityTypes { get; }

    /// <summary>
    /// Execute the pass over all qualifying entities.
    /// Processes entities in batches via the pipeline.
    /// </summary>
    Task ExecuteAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);
}
```

**Lifecycle**: Constructed by DI → dependency check → `ExecuteAsync` → done (no disposal needed — stateless).

**Error contract**: Throws on failure. No partial results. The phase runner halts.

**Thread safety**: Not thread-safe. One pass runs at a time within its modality. Passes across different modalities MAY run in parallel if their dependencies are satisfied.

**Implementors**: 7 text passes, 8 image passes, 22 audio passes, 6 video passes (see [analysis-passes.md](analysis-passes.md)).

---

## IRecomposer\<T\>

Generic interface for all recomposers. `T` is the output type — the format-specific result.

```csharp
public interface IRecomposer<T> where T : notnull
{
    /// <summary>
    /// Which modality this recomposer produces.
    /// </summary>
    Modality OutputModality { get; }

    /// <summary>
    /// Recompose an entity into its output format.
    /// Traverses composition → sequence → atoms to reconstruct content.
    /// 
    /// The traversal follows significance-weighted edges. Only edges above the
    /// significance threshold are followed. The path IS the explanation.
    /// </summary>
    /// <param name="entityId">Root entity to recompose from.</param>
    /// <param name="options">Traversal options: depth limit, significance threshold, arena filter.</param>
    /// <returns>The recomposed output.</returns>
    Task<T> RecomposeAsync(
        long entityId,
        RecompositionOptions options,
        CancellationToken ct);

    /// <summary>
    /// Recompose to a stream for large outputs (audio, video, model weights).
    /// The stream is written incrementally as the traversal progresses.
    /// </summary>
    Task RecomposeToStreamAsync(
        long entityId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct);
}

public sealed record RecompositionOptions
{
    public int MaxDepth { get; init; } = int.MaxValue;
    public double SignificanceThreshold { get; init; } = 0.0;
    public string? ArenaFilter { get; init; }
    public bool IncludeProvenance { get; init; } = false;
}
```

**`T` by implementor**:
| Recomposer | T | Description |
|------------|---|-------------|
| `TextRecomposer` | `string` | Reconstructed text |
| `ImageRecomposer` | `ImageBuffer` | Record: `byte[] Pixels`, `int Width`, `int Height`, `int Channels`, `PixelFormat Format` |
| `AudioRecomposer` | `AudioBuffer` | Record: `float[] Samples`, `int SampleRate`, `int Channels`, `int BitsPerSample` |
| `VideoRecomposer` | `VideoFrameSequence` | Record: `IReadOnlyList<ImageBuffer> Frames`, `AudioBuffer Audio`, `double FrameRate`, `TimeSpan Duration` |
| `SafetensorsRecomposer` | `SafetensorsFile` | Record: `IReadOnlyDictionary<string, TensorData> Tensors`, `string ModelName` (uses `RecomposeToStreamAsync` for serialization) |

**Thread safety**: Thread-safe. Recomposers are read-only graph traversals. Multiple concurrent recompositions are safe.

---

## IIngestionPipeline

The bridge between C# decomposer output and PostgreSQL stored procedures. Manages batching, transactions, and connection pooling.

```csharp
public interface IIngestionPipeline : IAsyncDisposable
{
    /// <summary>
    /// Create a new batch. The batch collects operations and submits them
    /// as a single transaction when committed.
    /// </summary>
    IIngestionBatch CreateBatch();

    /// <summary>
    /// Submit a batch to the database. Calls stored procedures in FK order:
    /// 1. batch_upsert_entities
    /// 2. batch_create_edges (which creates edge_members atomically)
    /// 3. populate_junction (per junction table)
    /// 4. create_physicality (per physicality)
    /// 5. create_sequence (per sequence entry)
    /// 6. initialize_significance (per significance entry)
    /// 
    /// All within a single transaction. Commits on success. Rolls back on any failure.
    /// </summary>
    Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct);

    /// <summary>
    /// Resolve entity IDs for a set of BLAKE3 hashes.
    /// Used after batch_upsert_entities to get IDs for edge/junction creation.
    /// Returns a dictionary of hash → entity_id.
    /// </summary>
    Task<IReadOnlyDictionary<byte[], long>> ResolveEntityIdsAsync(
        IReadOnlyList<byte[]> hashes,
        CancellationToken ct);

    /// <summary>
    /// Current pipeline statistics for monitoring.
    /// </summary>
    PipelineStats Stats { get; }
}
```

**Lifecycle**: Created once per decomposer run by the phase runner. Disposed after the decomposer completes. Owns the Npgsql connection pool.

**Error contract**: `SubmitBatchAsync` throws on any database error — constraint violation, connection failure, deadlock. The decomposer does not catch this. It propagates to the phase runner which halts.

**Thread safety**: `CreateBatch` is thread-safe. `SubmitBatchAsync` serializes submissions internally (one transaction at a time per connection).

**Implementor**: `NpgsqlIngestionPipeline`.

---

## IIngestionBatch

Represents a unit of work to submit atomically. Assembled by the decomposer, submitted by the pipeline.

```csharp
public interface IIngestionBatch
{
    /// <summary>
    /// Add an entity to the batch. Returns a batch-local handle for referencing
    /// this entity in edges and junctions before the real entity_id is known.
    /// </summary>
    EntityHandle AddEntity(byte[] hash, string entityTypeCode);

    /// <summary>
    /// Add an edge to the batch. Members reference EntityHandles or existing entity IDs.
    /// </summary>
    void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members);

    /// <summary>
    /// Add a junction entry to the batch.
    /// </summary>
    void AddJunction(
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu = null);

    /// <summary>
    /// Add a physicality entry to the batch.
    /// </summary>
    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geomWkb);

    /// <summary>
    /// Add a sequence entry to the batch.
    /// </summary>
    void AddSequence(
        EntityHandle parent,
        EntityHandle child,
        int position,
        int count = 1);

    /// <summary>
    /// Add a significance initialization entry to the batch.
    /// </summary>
    void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu);

    /// <summary>
    /// Number of entities in this batch.
    /// </summary>
    int EntityCount { get; }

    /// <summary>
    /// Number of edges in this batch.
    /// </summary>
    int EdgeCount { get; }
}

public readonly record struct EntityHandle(int BatchIndex);

public readonly record struct EdgeMemberSpec(
    EntityHandle? Handle,
    long? ExistingEntityId,
    string RoleCode,
    short Position);
```

**Thread safety**: Not thread-safe. One decomposer thread assembles one batch at a time.

---

## ISignificanceUpdater

Glicko-2 update primitive. Wraps the `record_comparison` stored procedure.

```csharp
public interface ISignificanceUpdater
{
    /// <summary>
    /// Record a comparison event between two entities or edges in an arena.
    /// Calls the stored procedure which handles Glicko-2 update and deadlock-preventing
    /// lock ordering internally.
    /// </summary>
    /// <param name="winnerId">Entity or edge ID that won the comparison.</param>
    /// <param name="loserId">Entity or edge ID that lost.</param>
    /// <param name="contextCode">Arena code (e.g., "lexical_disambiguation").</param>
    /// <param name="isEntity">True if comparing entities, false if comparing edges.</param>
    Task RecordComparisonAsync(
        long winnerId,
        long loserId,
        string contextCode,
        bool isEntity,
        CancellationToken ct);

    /// <summary>
    /// Initialize significance for a new entity or edge.
    /// Sets initial mu from the provenance trust prior.
    /// </summary>
    Task InitializeAsync(
        long targetId,
        string contextCode,
        double initialMu,
        bool isEntity,
        CancellationToken ct);

    /// <summary>
    /// Prune significance entries below threshold in an arena.
    /// Auditable — the stored procedure logs before deleting.
    /// </summary>
    Task<int> PruneBelowThresholdAsync(
        string contextCode,
        double muThreshold,
        CancellationToken ct);
}
```

**Thread safety**: Thread-safe. Multiple concurrent comparison events are supported. The stored procedure handles lock ordering.

**Implementor**: `GlickoSignificanceUpdater`.

---

## ITraversal

Traversal strategy for inference. Seeds, expands, scores, terminates.

```csharp
public interface ITraversal
{
    /// <summary>
    /// Execute a traversal from seed entities.
    /// 
    /// The traversal follows significance-weighted edges in BFS order.
    /// At each step, neighbors are expanded via the `neighbors` function,
    /// filtered by significance threshold and edge type. Path significance
    /// is the product of edge mu/1500 along the path (computed via log-sum-exp
    /// to prevent underflow).
    /// 
    /// Terminates when: cost budget exhausted, max depth reached, or no
    /// neighbors above threshold remain.
    /// </summary>
    Task<TraversalResult> TraverseAsync(
        TraversalQuery query,
        CancellationToken ct);
}

public sealed record TraversalQuery
{
    /// <summary>Entity IDs to start traversal from.</summary>
    public required IReadOnlyList<long> SeedEntityIds { get; init; }

    /// <summary>Maximum traversal depth.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>Minimum edge significance (mu) to follow.</summary>
    public double SignificanceThreshold { get; init; } = 1000.0;

    /// <summary>Maximum total cost budget (limits total work).</summary>
    public double CostBudget { get; init; } = 10_000.0;

    /// <summary>Edge type codes to follow. Null = all types.</summary>
    public IReadOnlyList<string>? EdgeTypeFilter { get; init; }

    /// <summary>Arena code for significance lookup.</summary>
    public required string ArenaCode { get; init; }
}

public sealed record TraversalResult
{
    public required IReadOnlyList<TraversalPath> Paths { get; init; }
    public int NodesVisited { get; init; }
    public double TotalCost { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed record TraversalPath
{
    public required IReadOnlyList<TraversalStep> Steps { get; init; }
    public double PathSignificance { get; init; }
}

public sealed record TraversalStep
{
    public long EntityId { get; init; }
    public long? EdgeId { get; init; }
    public string? EdgeTypeCode { get; init; }
    public double? EdgeMu { get; init; }
}
```

**Thread safety**: Thread-safe. Traversals are read-only MVCC-consistent queries.

**Implementor**: `SignificanceGuidedTraversal`.

---

## IPhaseRunner

CLI orchestrator. Runs phases in dependency order, manages decomposer lifecycle, handles failure.

```csharp
public interface IPhaseRunner
{
    /// <summary>
    /// Run a specific phase. Creates decomposer/pass instances via DI,
    /// validates sources, executes, reports progress.
    /// 
    /// Halts on any failure. No partial completion. No retry.
    /// </summary>
    Task<PhaseResult> RunPhaseAsync(Phase phase, CancellationToken ct);

    /// <summary>
    /// Run all phases in dependency order from the phase map.
    /// Phases with satisfied dependencies execute. A failure in any phase
    /// halts all subsequent phases.
    /// </summary>
    Task<IReadOnlyList<PhaseResult>> RunAllAsync(CancellationToken ct);

    /// <summary>
    /// Check which phases are complete, in-progress, or not started.
    /// Reads from the monitor schema.
    /// </summary>
    Task<IReadOnlyDictionary<Phase, PhaseStatus>> GetStatusAsync(CancellationToken ct);
}

public enum Phase
{
    CoreAlgebra,        // Phase 1 — schema + reference table seed
    UcdUca,             // Phase 2a
    Iso639,             // Phase 2b
    WordNetOmw,         // Phase 2c
    UniversalDeps,      // Phase 2d
    ModelDecomp,        // Phase 3
    Wiktionary,         // Phase 2e
    Tatoeba,            // Phase 2f
    SignificanceField,  // Phase 4
    InferenceEngine,    // Phase 5
    Validation          // Phase 6
}

public enum PhaseStatus { NotStarted, InProgress, Completed, Failed }

public sealed record PhaseResult(
    Phase Phase,
    PhaseStatus Status,
    TimeSpan Elapsed,
    string? ErrorMessage);
```

**Implementor**: `SequentialPhaseRunner`.

---

## IProgressReporter

How decomposers and passes report progress to the `monitor.ingestion_progress` table.

```csharp
public interface IProgressReporter
{
    /// <summary>
    /// Report current progress. Called periodically by decomposers (typically per-batch).
    /// Writes to monitor.ingestion_progress via the report_progress stored procedure.
    /// </summary>
    Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct);
}

public sealed record ProgressSnapshot
{
    public required string DecomposerCode { get; init; }
    public required string CurrentPhase { get; init; }
    public long EntitiesCreated { get; init; }
    public long EdgesCreated { get; init; }
    public long DuplicatesSkipped { get; init; }
    public long BytesProcessed { get; init; }
    public string? CurrentFile { get; init; }
    public int? CurrentBatch { get; init; }
}
```

**Thread safety**: Thread-safe. Progress reports are independent INSERT operations.

**Implementor**: `DatabaseProgressReporter`.

---

## IHealthCheck

Substrate health query interface. Wraps the `monitor.substrate_health` view.

```csharp
public interface IHealthCheck
{
    /// <summary>
    /// Query the substrate_health view and return structured health data.
    /// </summary>
    Task<SubstrateHealth> GetHealthAsync(CancellationToken ct);

    /// <summary>
    /// Query ingestion status for all active decomposers.
    /// </summary>
    Task<IReadOnlyList<IngestionStatus>> GetIngestionStatusAsync(CancellationToken ct);
}

public sealed record SubstrateHealth
{
    public long TotalEntities { get; init; }
    public long TotalEdges { get; init; }
    public IReadOnlyDictionary<string, long> EntitiesByType { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, double> MeanMuByArena { get; init; } = new Dictionary<string, double>();
    public long StorageSizeBytes { get; init; }
}

public sealed record IngestionStatus
{
    public required string DecomposerCode { get; init; }
    public long EntitiesCreated { get; init; }
    public long EdgesCreated { get; init; }
    public double EntitiesPerSecond { get; init; }
    public bool IsStuck { get; init; }
    public DateTimeOffset LastReport { get; init; }
}
```

**Implementor**: `SqlHealthCheck`.

---

## Interface Index

| Interface | Namespace | Implementors | Thread-safe |
|-----------|-----------|-------------|-------------|
| `IDecomposer` | `Hartonomous.Core.Decomposition` | 12 decomposers | No |
| `IAnalysisPass` | `Hartonomous.Core.Analysis` | 43 passes | No |
| `IRecomposer<T>` | `Hartonomous.Core.Recomposition` | 5 recomposers | Yes |
| `IIngestionPipeline` | `Hartonomous.Core.Ingestion` | `NpgsqlIngestionPipeline` | Partial |
| `IIngestionBatch` | `Hartonomous.Core.Ingestion` | `IngestionBatch` | No |
| `ISignificanceUpdater` | `Hartonomous.Core.Engine` | `GlickoSignificanceUpdater` | Yes |
| `ITraversal` | `Hartonomous.Core.Engine` | `SignificanceGuidedTraversal` | Yes |
| `IPhaseRunner` | `Hartonomous.Core.Orchestration` | `SequentialPhaseRunner` | No |
| `IProgressReporter` | `Hartonomous.Core.Monitoring` | `DatabaseProgressReporter` | Yes |
| `IHealthCheck` | `Hartonomous.Core.Monitoring` | `SqlHealthCheck` | Yes |
