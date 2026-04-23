using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Thin wrapper around substrate.upsert_* / link_entity_model_sources functions.
/// All DDL-level logic (ON CONFLICT, length checks, bulk link semantics) lives in
/// SQL migration 0021 — this file only marshals parameters.
/// </summary>
internal sealed class SafetensorsReferenceTableWriter : BaseReferenceTableWriter
{
    public SafetensorsReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter) : base(reader, junctionWriter, referenceDataWriter)
    {
    }

    public Task<Dictionary<string, int>> LoadTensorRoleMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.tensor_role", 64, ct);

    public Task<int> EnsureArchitectureClassAsync(string code, CancellationToken ct) =>
        EnsureArchitectureClassCoreAsync(code, ct);

    public Task<int> EnsureModelRegistryAsync(string code, string displayName, CancellationToken ct) =>
        EnsureModelRegistryCoreAsync(code, displayName, ct);

    public Task<int> EnsureModelPublisherAsync(
        int registryId, string slug, string? displayName, CancellationToken ct) =>
        EnsureModelPublisherCoreAsync(registryId, slug, displayName, ct);

    public Task<long> EnsureModelSourceAsync(
        int registryId, int publisherId, string modelSlug, byte[] revision, CancellationToken ct) =>
        EnsureModelSourceCoreAsync(registryId, publisherId, modelSlug, revision, ct);
}
