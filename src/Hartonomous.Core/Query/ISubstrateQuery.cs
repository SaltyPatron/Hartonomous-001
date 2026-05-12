using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Query;

/// <summary>
/// Composable read API over substrate state. Used by recomposers (and other
/// downstream consumers) to express the WHERE clause of distillation per
/// architecture.md ("Distillation = WHERE clause + type/modality/trust constraints").
///
/// Hash-as-PK throughout: queries emit composite <see cref="EntityHandle"/>
/// results. Filter dimensions compose via SQL JOINs in the implementation.
/// </summary>
public interface ISubstrateQuery
{
    /// <summary>
    /// Find entities matching the filter, ordered by significance mu descending
    /// when a context is specified, by hash ascending otherwise.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QueryEntitiesAsync(
        SubstrateQueryFilter filter, CancellationToken ct);

    /// <summary>
    /// Find tensor entities attached to the given model_architecture entity
    /// via has_tensor edges, optionally filtered by source/significance.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QueryTensorsForArchitectureAsync(
        EntityHandle modelArchitecture,
        SubstrateQueryFilter filter,
        CancellationToken ct);

    /// <summary>
    /// Return tensors in deterministic enumeration order for one concrete
    /// ingested package/model_source, via sequence(model_package -> tensor).
    /// </summary>
    Task<IReadOnlyList<PackageTensorHandle>> QueryTensorsForModelSourceAsync(
        long modelSourceId,
        CancellationToken ct);

    /// <summary>
    /// Token entities carrying embedding_firefly physicality, narrowed to the
    /// supplied vocabulary set and significance threshold in the chosen arena.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QueryFireflyForVocabAsync(
        IReadOnlyList<EntityHandle> bpeTokens,
        double minSignificanceMu,
        string contextTypeCode,
        int? limit,
        CancellationToken ct);

    /// <summary>
    /// ffn_neuron entities sourced from FFN tensors whose hidden dimension
    /// matches <paramref name="hiddenSize"/>, ranked top-K by significance.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QueryFfnNeuronsByHiddenDimAsync(
        int hiddenSize,
        int topK,
        string contextTypeCode,
        CancellationToken ct);

    /// <summary>
    /// attention_component entities sourced from attention tensors matching
    /// the given head dimension, optionally narrowed to one archetype.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QueryAttentionComponentsAsync(
        int headDim,
        EntityHandle? archetype,
        int topK,
        string contextTypeCode,
        CancellationToken ct);

    /// <summary>
    /// svd_rank_component entities for tensors of the given tensor-role,
    /// ranked by σ (descending).
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct);
}
