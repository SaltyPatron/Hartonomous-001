using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;

namespace Hartonomous.Core.Decomposition;

public interface IDecomposer : IAsyncDisposable
{
    string ProvenanceCode { get; }

    string DisplayName { get; }

    IReadOnlyList<Phase> Phases { get; }

    Task ValidateSourceAsync(CancellationToken ct);

    Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);
}
