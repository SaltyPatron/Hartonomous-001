using Hartonomous.Core.Ingestion;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// The per-model <c>model_architecture</c> entity, already created and
/// persisted by the orchestrator's pre-pass setup. Carries the architecture
/// content hash and the substrate <see cref="EntityHandle"/> (hash + type
/// code) so passes can attach edges by referencing the architecture as a
/// regular EdgeMemberSpec — no surrogate id, no cross-batch resolve.
/// </summary>
public sealed record ModelArchitectureHandle(
    ModelArchitecture Architecture,
    int ArchitectureClassId,
    byte[] ContentHash,
    EntityHandle Entity);
