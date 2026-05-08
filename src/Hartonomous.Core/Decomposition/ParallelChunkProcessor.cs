using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Multi-threaded producer scaffolding for streaming decomposers. The
/// existing single-producer pattern leaves the host's 24-core class hardware
/// at 5%-of-1-core utilization because every decomposer runs as ONE thread
/// pushing to the pipeline's bounded channels — drains have 10 worker threads
/// but they all wait on the single producer.
///
/// This processor lets a decomposer fan its chunk stream out across N
/// worker tasks. Each task pulls a chunk from the source enumerable, runs
/// the precompute → bulk-check → emit-diff orchestration, and pushes
/// records into the (thread-safe) IRecordSink.EmitAsync.
///
/// Thread-safety contract:
///   - IRecordSink.EmitAsync is MPSC-safe by design (the pipeline's bounded
///     channels accept concurrent writers).
///   - IIngestionPipeline.GetExisting*Async each open their own
///     NpgsqlConnection — concurrent calls are fine.
///   - The decomposer's per-chunk state must be local to the chunk; no
///     shared mutable state across worker tasks.
/// </summary>
public static class ParallelChunkProcessor
{
    /// <summary>
    /// Process an async source of chunks across <paramref name="degreeOfParallelism"/>
    /// worker tasks. Each chunk is handed to <paramref name="processChunk"/>
    /// which performs the substrate-aware ingestion (precompute candidates,
    /// bulk-check, emit diff, fire rating events).
    /// </summary>
    public static async Task RunAsync<TChunk>(
        IAsyncEnumerable<TChunk> source,
        Func<TChunk, CancellationToken, ValueTask> processChunk,
        int degreeOfParallelism,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processChunk);
        ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);

        ParallelOptions opts = new()
        {
            MaxDegreeOfParallelism = degreeOfParallelism,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(source, opts, processChunk).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous-source variant for decomposers whose chunk source is a
    /// regular IEnumerable (parsed in-memory, e.g. a List&lt;SynsetRecord&gt;).
    /// </summary>
    public static async Task RunAsync<TChunk>(
        IEnumerable<TChunk> source,
        Func<TChunk, CancellationToken, ValueTask> processChunk,
        int degreeOfParallelism,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processChunk);
        ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);

        ParallelOptions opts = new()
        {
            MaxDegreeOfParallelism = degreeOfParallelism,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(source, opts, processChunk).ConfigureAwait(false);
    }

    /// <summary>
    /// Default degree-of-parallelism heuristic for ingestion-bound work:
    /// half the host's logical core count, clamped to [4, 16]. Leaves
    /// headroom for the pipeline's 10 drain tasks plus PG backends.
    /// </summary>
    public static int DefaultDegreeOfParallelism()
    {
        int cores = Environment.ProcessorCount;
        int p = cores / 2;
        if (p < 4)
        {
            p = 4;
        }
        if (p > 16)
        {
            p = 16;
        }
        return p;
    }
}
