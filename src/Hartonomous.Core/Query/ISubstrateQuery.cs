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
}
