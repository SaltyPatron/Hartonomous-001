using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-pass-execution batch lifecycle wrapper. Owns the current
/// <see cref="IIngestionBatch"/>; auto-flushes when the batch grows past the
/// orchestrator's threshold; re-creates the model_architecture handle in each
/// fresh batch (since handles are batch-scoped).
///
/// Passes write through this surface rather than directly against the pipeline
/// so the orchestrator owns commit boundaries and per-pass entity/edge counts.
/// </summary>
public interface IPassSession
{
    /// <summary>The current batch — handles obtained here are valid until the next flush.</summary>
    IIngestionBatch Batch { get; }

    /// <summary>Handle to the model_architecture entity in <see cref="Batch"/>. Re-created on every flush.</summary>
    EntityHandle ModelEntity { get; }

    /// <summary>Total entities the pass has appended across all flushed + pending batches.</summary>
    long EntitiesCreated { get; }

    /// <summary>Total edges the pass has appended across all flushed + pending batches.</summary>
    long EdgesCreated { get; }

    /// <summary>Flushes if <see cref="IIngestionBatch.EntityCount"/> ≥ <paramref name="threshold"/> or <see cref="IIngestionBatch.EdgeCount"/> ≥ <paramref name="threshold"/>.</summary>
    Task MaybeFlushAsync(int threshold, CancellationToken ct);

    /// <summary>Unconditional flush. Caller may continue using the session afterwards — a fresh batch is started.</summary>
    Task FlushAsync(CancellationToken ct);
}
