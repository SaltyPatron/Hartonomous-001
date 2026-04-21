using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Ingestion;

public interface IIngestionPipeline : IAsyncDisposable
{
    IIngestionBatch CreateBatch();

    Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct);

    Task<IReadOnlyDictionary<byte[], long>> ResolveEntityIdsAsync(
        IReadOnlyList<byte[]> hashes,
        CancellationToken ct);

    /// <summary>
    /// Populate edge trajectory geometry for all edges whose trajectories are not yet set.
    /// Call once at the end of a decomposition phase rather than per-batch.
    /// </summary>
    Task PopulateEdgeTrajectoriesAsync(CancellationToken ct);

    PipelineStats Stats { get; }
}
