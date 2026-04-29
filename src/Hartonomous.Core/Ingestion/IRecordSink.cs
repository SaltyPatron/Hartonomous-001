using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// The streaming-pipeline producer-side surface. Decomposers receive an
/// <see cref="IRecordSink"/> and emit one <see cref="IngestionRecord"/> at a
/// time. There is no batch boundary in this API. The sink is responsible for:
///
///   * Routing each record to its per-kind bounded channel
///   * Backpressure (EmitAsync awaits when its channel is full — natural
///     producer throttling to consumer throughput)
///   * Long-lived NpgsqlBinaryImporter streams per substrate destination
///   * Chunk-amortized COPY commits (~4096 rows or ~250ms idle, whichever first)
///   * Background drain of staging→substrate via the substrate.drain_staging_*
///     functions
///   * Background significance priming via substrate.prime_unprimed_edges_chunk
///
/// Concretely replaces <see cref="IIngestionPipeline.SubmitBatchAsync(IIngestionBatch, CancellationToken)"/>:
/// where the old API exposed batches and per-batch transactions, this exposes
/// records and continuous flow. Decomposers no longer accumulate in memory
/// past a single record's worth — record produced → record emitted → record
/// in channel → C# memory free.
///
/// Lifecycle: callers MUST call <see cref="FlushAsync"/> before the sink is
/// disposed to drain in-flight channel contents into staging tables. The
/// background drain workers continue draining staging→substrate after that
/// point until staging is empty. The sink itself is IAsyncDisposable; Dispose
/// completes channels and joins drain tasks.
/// </summary>
public interface IRecordSink
{
    /// <summary>
    /// Submit one record into the streaming pipeline. Returns when the record
    /// is enqueued in its destination channel (or if the channel is full,
    /// when capacity becomes available — natural backpressure).
    ///
    /// Thread-safe. Multiple decomposer tasks may emit concurrently.
    /// </summary>
    ValueTask EmitAsync(IngestionRecord record, CancellationToken ct);

    /// <summary>
    /// Force-flush all in-flight channel contents to staging tables. Returns
    /// when every channel is drained AND every per-kind COPY stream has
    /// committed its current chunk. Does NOT wait for staging→substrate drain
    /// (that's the background worker's responsibility and runs continuously).
    ///
    /// Call this at the end of a decomposer / phase / shutdown to guarantee
    /// every emitted record has reached the staging persistence boundary.
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct);
}
