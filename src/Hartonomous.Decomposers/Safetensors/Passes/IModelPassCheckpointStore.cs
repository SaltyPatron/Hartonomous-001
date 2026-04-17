using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Persistence surface for <c>substrate.model_pass_checkpoint</c> rows. Pure
/// stored-procedure passthrough — no SQL composition in C#. Per CLAUDE.md
/// § "Database Operations / Batch Everything" + § "No inline SQL".
/// </summary>
public interface IModelPassCheckpointStore
{
    /// <summary>Returns the pass_ids that have already completed for this model.</summary>
    Task<IReadOnlySet<string>> LoadCompletedPassIdsAsync(long modelSourceId, CancellationToken ct);

    /// <summary>Records a successful pass run (stamps completed_at).</summary>
    Task MarkCompletedAsync(long modelSourceId, string passId, long entityCount, long edgeCount, CancellationToken ct);

    /// <summary>Records a pass start or failure (clears completed_at, stores error if any).</summary>
    Task MarkInFlightAsync(long modelSourceId, string passId, long entityCount, long edgeCount, string? lastError, CancellationToken ct);
}
