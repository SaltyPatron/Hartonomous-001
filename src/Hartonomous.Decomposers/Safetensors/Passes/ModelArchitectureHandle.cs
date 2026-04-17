namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// The per-model <c>model_architecture</c> entity, already created and persisted
/// by the orchestrator's pre-pass setup. Carries the architecture content hash
/// and the resolved entity_id so passes can attach edges via
/// <see cref="Hartonomous.Core.Ingestion.EdgeMemberSpec"/> with
/// <c>ExistingEntityId</c> set rather than re-creating the entity in their batch.
/// </summary>
public sealed record ModelArchitectureHandle(
    ModelArchitecture Architecture,
    int ArchitectureClassId,
    byte[] ContentHash,
    long EntityId);
