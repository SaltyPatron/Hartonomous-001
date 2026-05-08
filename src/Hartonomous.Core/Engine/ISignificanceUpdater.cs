using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

public interface ISignificanceUpdater
{
    Task RecordEntityComparisonAsync(
        EntityHandle winner,
        EntityHandle loser,
        string contextCode,
        CancellationToken ct);

    Task RecordEdgeComparisonAsync(
        EdgeHandle winner,
        EdgeHandle loser,
        string contextCode,
        CancellationToken ct);

    Task InitializeEntityAsync(
        EntityHandle target,
        string contextCode,
        double initialMu,
        CancellationToken ct);

    Task InitializeEdgeAsync(
        EdgeHandle target,
        string contextCode,
        double initialMu,
        CancellationToken ct);

    Task<int> PruneBelowThresholdAsync(
        string contextCode,
        double muThreshold,
        CancellationToken ct);
}
