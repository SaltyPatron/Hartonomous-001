# Base Classes

**Status**: ✅ Complete

Abstract base classes providing shared behavior for decomposers, recomposers, and analysis passes. Every concrete implementation inherits from one of these.

---

## BaseDecomposer

Implements `IDecomposer`. Provides BLAKE3 hashing, batch assembly, progress reporting, and configuration. Every seed and runtime decomposer inherits from this.

```csharp
public abstract class BaseDecomposer : IDecomposer
{
    private readonly DecomposerConfig _config;
    private readonly ILogger _logger;

    // --- IDecomposer Properties ---
    public abstract string ProvenanceCode { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<Phase> Phases { get; }

    protected BaseDecomposer(DecomposerConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    // --- IDecomposer Methods ---

    /// <summary>
    /// Default implementation checks that all source paths in config exist.
    /// Override to add format-specific validation (e.g., parse headers, check file counts).
    /// </summary>
    public virtual Task ValidateSourceAsync(CancellationToken ct)
    {
        foreach (var path in GetSourcePaths())
        {
            if (!Path.Exists(path))
                throw new SourceValidationException(ProvenanceCode, $"Source not found: {path}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Template method. Calls DecomposeCore in a structured context with
    /// automatic progress reporting at batch boundaries.
    /// </summary>
    public async Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting decomposition: {Decomposer}", DisplayName);

        await DecomposeCoreAsync(pipeline, reporter, ct);

        _logger.LogInformation("Completed decomposition: {Decomposer}", DisplayName);
    }

    // --- Abstract Methods (subclasses MUST implement) ---

    /// <summary>
    /// Core decomposition logic. Subclass implements the actual parsing and ingestion.
    /// </summary>
    protected abstract Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);

    /// <summary>
    /// Return all source paths this decomposer needs.
    /// Used by ValidateSourceAsync.
    /// </summary>
    protected abstract IReadOnlyList<string> GetSourcePaths();

    // --- Protected Helpers (subclasses use these) ---

    /// <summary>
    /// Compute BLAKE3 hash of raw bytes. Calls the native shared library via P/Invoke.
    /// All decomposers use this — never compute hashes independently.
    /// </summary>
    protected byte[] ComputeHash(ReadOnlySpan<byte> content)
        => Blake3Native.Hash(content);

    /// <summary>
    /// Compute BLAKE3 hash of a string (UTF-8 encoded).
    /// </summary>
    protected byte[] ComputeHash(string content)
        => Blake3Native.Hash(Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Compute Merkle hash of ordered child hashes.
    /// For compositions: hash = BLAKE3(child1_hash || child2_hash || ... || childN_hash).
    /// </summary>
    protected byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes)
    {
        var concat = new byte[childHashes.Length * 32];
        for (int i = 0; i < childHashes.Length; i++)
            childHashes[i].CopyTo(concat.AsSpan(i * 32));
        return Blake3Native.Hash(concat);
    }

    /// <summary>
    /// Compute edge hash: BLAKE3(edge_type_id || participant_hashes_in_role_order).
    /// </summary>
    protected byte[] ComputeEdgeHash(
        int edgeTypeId,
        ReadOnlySpan<byte[]> participantHashes)
    {
        var buffer = new byte[4 + participantHashes.Length * 32];
        BitConverter.TryWriteBytes(buffer, edgeTypeId);
        for (int i = 0; i < participantHashes.Length; i++)
            participantHashes[i].CopyTo(buffer.AsSpan(4 + i * 32));
        return Blake3Native.Hash(buffer);
    }

    /// <summary>
    /// Submit a batch and report progress. Call this at regular intervals
    /// (e.g., every N entities) to maintain backpressure and progress visibility.
    /// </summary>
    protected async Task SubmitAndReportAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        IIngestionBatch batch,
        ProgressSnapshot snapshot,
        CancellationToken ct)
    {
        await pipeline.SubmitBatchAsync(batch, ct);
        await reporter.ReportAsync(snapshot, ct);
    }

    /// <summary>
    /// Configured batch size. Default 10,000. Override per decomposer via config.
    /// </summary>
    protected int BatchSize => _config.BatchSize;

    // --- IAsyncDisposable ---
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DecomposerConfig
{
    public required string SourceDirectory { get; init; }
    public int BatchSize { get; init; } = 10_000;
    public string ConnectionString { get; init; } = "Host=localhost;Port=5432;Database=hartonomous;Username=postgres;Password=postgres";
}
```

### What Subclasses Get for Free

| Capability | Method | Details |
|------------|--------|---------|
| BLAKE3 hashing | `ComputeHash` | P/Invoke to libhartonomous. SIMD-accelerated. |
| Merkle hashing | `ComputeMerkleHash` | Ordered concatenation of child hashes → BLAKE3. |
| Edge hashing | `ComputeEdgeHash` | edge_type_id + participant hashes → BLAKE3. |
| Source validation | `ValidateSourceAsync` | Checks all `GetSourcePaths()` exist. Override for format-specific checks. |
| Batch submission + progress | `SubmitAndReportAsync` | One call to submit batch and report progress. |
| Configuration | `BatchSize`, `_config` | Injected via constructor. |
| Logging | `_logger` | Structured logging via `ILogger`. |

### What Subclasses Must Implement

| Method | Purpose |
|--------|---------|
| `ProvenanceCode` | Return provenance table code (e.g., `"princeton_wordnet"`). |
| `DisplayName` | Human-readable name for progress reporting. |
| `Phases` | Which phases this decomposer runs in. |
| `GetSourcePaths` | All source file/directory paths needed. |
| `DecomposeCoreAsync` | The actual parsing and batch assembly logic. |

---

## BaseRecomposer\<T\>

Implements `IRecomposer<T>`. Provides entity retrieval, edge traversal, junction lookup, physicality retrieval, and sequence retrieval helpers.

```csharp
public abstract class BaseRecomposer<T> : IRecomposer<T> where T : notnull
{
    private readonly NpgsqlDataSource _dataSource;

    public abstract Modality OutputModality { get; }

    protected BaseRecomposer(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // --- IRecomposer<T> ---

    public abstract Task<T> RecomposeAsync(
        long entityId,
        RecompositionOptions options,
        CancellationToken ct);

    public virtual Task RecomposeToStreamAsync(
        long entityId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support streaming recomposition.");

    // --- Protected Helpers ---

    /// <summary>
    /// Retrieve entity by ID. Returns entity_type_code and hash.
    /// Calls entity_by_hash or direct SELECT.
    /// </summary>
    protected async Task<EntityRecord?> GetEntityAsync(long entityId, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT e.id, e.hash, et.code FROM substrate.entity e " +
            "JOIN substrate.entity_type et ON e.entity_type_id = et.id " +
            "WHERE e.id = $1");
        cmd.Parameters.AddWithValue(entityId);
        // ... execute and map
    }

    /// <summary>
    /// Get ordered children of a composition entity from the sequence table.
    /// Returns child entity IDs in position order, with RLE counts.
    /// </summary>
    protected async Task<IReadOnlyList<SequenceEntry>> GetChildrenAsync(
        long parentId, CancellationToken ct)
    {
        // SELECT child_id, position, count FROM substrate.sequence
        // WHERE parent_id = $1 ORDER BY position
    }

    /// <summary>
    /// Get outbound edges from an entity, filtered by type and significance.
    /// </summary>
    protected async Task<IReadOnlyList<EdgeRecord>> GetEdgesAsync(
        long entityId,
        string? edgeTypeFilter,
        double significanceThreshold,
        CancellationToken ct)
    {
        // Calls the neighbors() function with significance filtering
    }

    /// <summary>
    /// Get all physicality rows for an entity.
    /// </summary>
    protected async Task<IReadOnlyList<PhysicalityRecord>> GetPhysicalitiesAsync(
        long entityId, CancellationToken ct)
    {
        // SELECT * FROM substrate.physicality WHERE entity_id = $1
    }

    /// <summary>
    /// Get junction table entries for an entity.
    /// E.g., POS classifications, sense assignments, language tags.
    /// </summary>
    protected async Task<IReadOnlyList<JunctionRecord>> GetJunctionsAsync(
        long entityId, string junctionTable, CancellationToken ct)
    {
        // Dynamic but whitelisted query against the specified junction table
    }

    /// <summary>
    /// Recursively walk the sequence tree to reconstruct the full atom list.
    /// Expands RLE counts. Returns atoms in document order.
    /// </summary>
    protected async Task<IReadOnlyList<long>> FlattenToAtomsAsync(
        long compositionId, CancellationToken ct)
    {
        // Recursive: GetChildren → for each child, if composition → recurse
        // If atom → add to list (repeated by count)
    }
}

public sealed record EntityRecord(long Id, byte[] Hash, string TypeCode);
public sealed record SequenceEntry(long ChildId, int Position, int Count);
public sealed record EdgeRecord(long EdgeId, string TypeCode, long TargetId, double? Mu);
public sealed record PhysicalityRecord(long Id, string TypeCode, byte[] GeomWkb);
public sealed record JunctionRecord(long EntityId, int ReferenceId, double? Mu);
```

### What Recomposer Subclasses Get for Free

| Capability | Method |
|------------|--------|
| Entity lookup | `GetEntityAsync` |
| Ordered children | `GetChildrenAsync` |
| Edge traversal | `GetEdgesAsync` |
| Physicality | `GetPhysicalitiesAsync` |
| Junction lookup | `GetJunctionsAsync` |
| Recursive flattening | `FlattenToAtomsAsync` |

### What Subclasses Must Implement

| Method | Purpose |
|--------|---------|
| `OutputModality` | Which modality this recomposer produces. |
| `RecomposeAsync` | Traverse the entity graph and produce `T`. |
| `RecomposeToStreamAsync` | Override for large output formats (optional). |

---

## BaseAnalysisPass

Implements `IAnalysisPass`. Provides entity querying, batch creation, and dependency checking.

```csharp
public abstract class BaseAnalysisPass : IAnalysisPass
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;

    public abstract string PassId { get; }
    public abstract Modality Modality { get; }
    public abstract IReadOnlyList<string> Dependencies { get; }
    public abstract IReadOnlyList<string> InputEntityTypes { get; }

    protected BaseAnalysisPass(NpgsqlDataSource dataSource, ILogger logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    // --- IAnalysisPass ---

    public async Task ExecuteAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting pass: {PassId}", PassId);

        await ExecuteCoreAsync(pipeline, reporter, ct);

        _logger.LogInformation("Completed pass: {PassId}", PassId);
    }

    // --- Abstract ---

    /// <summary>
    /// Core pass logic. Query entities by InputEntityTypes, analyze, write results
    /// as new edges/physicalities/significance via the pipeline.
    /// </summary>
    protected abstract Task ExecuteCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);

    // --- Protected Helpers ---

    /// <summary>
    /// Query entities of specified types in batches.
    /// Returns entity IDs in pages for processing.
    /// </summary>
    protected async IAsyncEnumerable<IReadOnlyList<long>> QueryEntitiesInBatchesAsync(
        IReadOnlyList<string> entityTypeCodes,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // SELECT e.id FROM substrate.entity e
        // JOIN substrate.entity_type et ON e.entity_type_id = et.id
        // WHERE et.code = ANY($1)
        // ORDER BY e.id
        // LIMIT $2 OFFSET ...
        // Yields pages of entity IDs
    }

    /// <summary>
    /// Check if a prerequisite pass has completed.
    /// Queries the monitor schema for pass completion records.
    /// </summary>
    protected async Task<bool> IsDependencySatisfiedAsync(
        string passId, CancellationToken ct)
    {
        // SELECT EXISTS(SELECT 1 FROM monitor.pass_completion WHERE pass_id = $1)
    }

    /// <summary>
    /// Get all edges of specific types involving an entity.
    /// Used by passes that need to read existing structure.
    /// </summary>
    protected async Task<IReadOnlyList<EdgeRecord>> GetEdgesByTypeAsync(
        long entityId, string edgeTypeCode, CancellationToken ct)
    {
        // Query edge + edge_member for edges involving this entity with the given type
    }

    /// <summary>
    /// Get a connection from the pool for direct queries.
    /// Use sparingly — prefer the pipeline for writes.
    /// </summary>
    protected NpgsqlDataSource DataSource => _dataSource;
}
```

### What Pass Subclasses Get for Free

| Capability | Method |
|------------|--------|
| Batched entity iteration | `QueryEntitiesInBatchesAsync` |
| Dependency checking | `IsDependencySatisfiedAsync` |
| Edge reading | `GetEdgesByTypeAsync` |
| Structured logging | `_logger` |
| Database access | `DataSource` |

### What Subclasses Must Implement

| Method | Purpose |
|--------|---------|
| `PassId` | Unique identifier for tracking and dependency resolution. |
| `Modality` | Which modality this pass serves. |
| `Dependencies` | List of pass IDs that must run first. |
| `InputEntityTypes` | Entity type codes this pass consumes. |
| `ExecuteCoreAsync` | The analysis logic — read entities, produce new edges/physicalities. |

---

## Base Class Index

| Base Class | Implements | Namespace | Subclass Count |
|------------|-----------|-----------|----------------|
| `BaseDecomposer` | `IDecomposer` | `Hartonomous.Core.Decomposition` | 12 |
| `BaseRecomposer<T>` | `IRecomposer<T>` | `Hartonomous.Core.Recomposition` | 5 |
| `BaseAnalysisPass` | `IAnalysisPass` | `Hartonomous.Core.Analysis` | 43 |
