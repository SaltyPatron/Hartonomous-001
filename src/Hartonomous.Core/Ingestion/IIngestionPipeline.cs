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

    PipelineStats Stats { get; }
}
