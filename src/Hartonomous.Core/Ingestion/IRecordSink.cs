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
///   * Chunk-amortized COPY commits (~32_768 rows or ~250ms idle, whichever first)
///   * Each drain task holds its own NpgsqlConnection; each chunk:
///       TRUNCATE pg_temp.X_inflight
///       COPY pg_temp.X_inflight FROM STDIN (FORMAT binary)
///       INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING
///
/// There is NO background staging drain and NO background significance primer.
/// Both were removed. End-of-phase post-passes (edge trajectory backfill and
/// significance priming) are owned by the phase orchestrator, not this interface.
///
/// Lifecycle: callers MUST call <see cref="FlushAsync"/> before the sink is
/// disposed to drain in-flight channel contents into substrate. FlushAsync
/// marks all channels complete, waits for drain tasks to finish their final
/// chunks, then returns. The sink itself is IAsyncDisposable; Dispose joins
/// drain tasks via FlushAsync.
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
    /// Force-flush all in-flight channel contents to substrate. Returns when
    /// every channel is drained AND every per-kind COPY stream has committed
    /// its final chunk into substrate via the INSERT-SELECT step. After this
    /// returns, every emitted record is in substrate.
    ///
    /// Call this at the end of a decomposer / phase / shutdown to guarantee
    /// every emitted record has reached the persistence boundary. Post-phase
    /// enrichment (edge trajectory backfill, significance priming) is NOT
    /// performed by this method — that is the phase orchestrator's responsibility.
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct);
}
