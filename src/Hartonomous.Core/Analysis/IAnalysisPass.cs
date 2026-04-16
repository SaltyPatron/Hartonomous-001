using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;

namespace Hartonomous.Core.Analysis;

public interface IAnalysisPass
{
    string PassId { get; }

    Modality Modality { get; }

    IReadOnlyList<string> Dependencies { get; }

    IReadOnlyList<string> InputEntityTypes { get; }

    Task ExecuteAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);
}
