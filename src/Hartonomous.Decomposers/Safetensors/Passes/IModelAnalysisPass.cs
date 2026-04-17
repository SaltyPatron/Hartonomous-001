using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// One DAG-orderable, checkpointable analysis pass over a single model. Per
/// docs/specs/decomposers/analysis-passes.md.
///
/// Determinism: <see cref="RunAsync"/> must be bitwise reproducible on the same
/// model + same substrate state. Any seeded numerical primitive must derive
/// its seed from <see cref="ModelPassContext.CheckpointKey"/> via the canonical
/// signature builder. Throwing halts only the current model — the orchestrator
/// records the failure on (model_source_id, pass_id) and proceeds to the next
/// model in the discovery list.
/// </summary>
public interface IModelAnalysisPass
{
    /// <summary>Stable id, format <c>"model.{snake_name}"</c>. Used for checkpointing and dependency resolution.</summary>
    string PassId { get; }

    /// <summary>Pass ids that must complete on the SAME model before this one runs.</summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>Architecture class codes this pass applies to. Empty list = applies to all.</summary>
    IReadOnlyList<string> AppliesToArchitectures { get; }

    Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct);
}
