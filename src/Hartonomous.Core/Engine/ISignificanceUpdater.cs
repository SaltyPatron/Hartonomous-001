using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Engine;

public interface ISignificanceUpdater
{
    Task RecordComparisonAsync(
        long winnerId,
        long loserId,
        string contextCode,
        bool isEntity,
        CancellationToken ct);

    Task InitializeAsync(
        long targetId,
        string contextCode,
        double initialMu,
        bool isEntity,
        CancellationToken ct);

    Task<int> PruneBelowThresholdAsync(
        string contextCode,
        double muThreshold,
        CancellationToken ct);
}
