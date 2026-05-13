using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Bootstraps the per-model pass context used by every
/// <see cref="IModelAnalysisPass"/> in the orchestrator. Extracted from
/// <see cref="ModelPassOrchestrator"/> per the S3.I split so bootstrap
/// concerns (architecture detection, signature building, tensor enumeration,
/// donor-package bridging, initial entity emission) sit separately from
/// pass-ordering concerns.
///
/// <para>
/// Determinism: <see cref="BootstrapAsync"/> must produce the same
/// <see cref="ModelPassContext"/> for the same input model — same
/// <c>model_architecture</c> hash, same <c>model_package</c> hash, same
/// tensor enumeration order, same canonical-signature seed. Law #6 applies
/// across the bootstrap surface.
/// </para>
/// </summary>
internal interface IModelBootstrap
{
    /// <summary>
    /// Detect the model's architecture, compute its canonical signatures,
    /// enumerate its tensors, emit the <c>model_architecture</c> +
    /// <c>model_package</c> entities and their associated junctions/edges,
    /// and return the <see cref="ModelPassContext"/> subsequent passes will
    /// share for this model.
    /// </summary>
    Task<ModelPassContext> BootstrapAsync(
        DiscoveredModel model,
        long modelSourceId,
        CancellationToken ct);
}
