using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Orchestration;

public interface IPhaseRunner
{
    Task<PhaseResult> RunPhaseAsync(Phase phase, CancellationToken ct);

    Task<IReadOnlyList<PhaseResult>> RunAllAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<Phase, PhaseStatus>> GetStatusAsync(CancellationToken ct);
}
