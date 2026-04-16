using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Monitoring;

public interface IHealthCheck
{
    Task<SubstrateHealth> GetHealthAsync(CancellationToken ct);

    Task<IReadOnlyList<IngestionStatus>> GetIngestionStatusAsync(CancellationToken ct);
}
