using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Query;

/// <summary>
/// Composable read API over substrate state. Used by recomposers (and other
/// downstream consumers) to express the WHERE clause of distillation per
/// architecture.md ("Distillation = WHERE clause + type/modality/trust constraints").
///
/// Backed by the substrate's existing tables (substrate.entity, substrate.edge,
/// substrate.significance, substrate.entity_model_source) — no new storage.
/// Filter dimensions compose via SQL JOINs in the implementation.
/// </summary>
public interface ISubstrateQuery
{
    /// <summary>
    /// Find entities matching the filter, ordered by significance mu descending
    /// when a context is specified, by entity id ascending otherwise.
    /// Returns (entity_id, entity_type_code) pairs.
    /// </summary>
    Task<IReadOnlyList<(long EntityId, string EntityTypeCode)>> QueryEntitiesAsync(
        SubstrateQueryFilter filter, CancellationToken ct);

    /// <summary>
    /// Find tensor entities attached to the given model_architecture entity
    /// via has_tensor edges, optionally filtered by source/significance.
    /// Used by SafetensorsRecomposer for distilled exports — "all tensors of
    /// this architecture, filtered to Qwen-Coder evidence with significance > 1500."
    /// </summary>
    Task<IReadOnlyList<long>> QueryTensorsForArchitectureAsync(
        long modelArchitectureEntityId,
        SubstrateQueryFilter filter,
        CancellationToken ct);

    /// <summary>
    /// All <c>embedding_firefly</c> entities anchored to the given bpe_token
    /// vocabulary set, above the given significance threshold in the chosen
    /// arena. Powers vocab-scoped distillation — "give me firefly positions
    /// only for tokens this target architecture's tokenizer covers."
    /// Per docs/specs/csharp/recomposers.md.
    /// </summary>
    Task<IReadOnlyList<long>> QueryFireflyForVocabAsync(
        IReadOnlyList<long> bpeTokenEntityIds,
        double minSignificanceMu,
        string contextTypeCode,
        int? limit,
        CancellationToken ct);

    /// <summary>
    /// <c>ffn_neuron</c> entities sourced from FFN tensors whose hidden
    /// dimension matches <paramref name="hiddenSize"/>, ranked top-K by
    /// significance. The hidden dim filter walks the tensor's has_shape
    /// edge so neurons from architectures with the wrong hidden dim are
    /// excluded — the recomposer needs row-shape compatibility before
    /// scatter.
    /// </summary>
    Task<IReadOnlyList<long>> QueryFfnNeuronsByHiddenDimAsync(
        int hiddenSize,
        int topK,
        string contextTypeCode,
        CancellationToken ct);

    /// <summary>
    /// <c>attention_component</c> entities sourced from attention tensors
    /// matching the given head dimension, optionally narrowed to one
    /// archetype id (from AttentionArchetypePass). Top-K by significance.
    /// </summary>
    Task<IReadOnlyList<long>> QueryAttentionComponentsAsync(
        int headDim,
        long? archetypeEntityId,
        int topK,
        string contextTypeCode,
        CancellationToken ct);

    /// <summary>
    /// <c>svd_rank_component</c> entities for tensors of the given tensor-
    /// role, ranked by σ (descending). Drives operator-reconstruction
    /// distillation when full-row units aren't available for the target
    /// shape. SvdPass emits in descending-σ edge order, so the
    /// has_rank_component edge ordinal is the sigma rank.
    /// </summary>
    Task<IReadOnlyList<long>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct);
}
