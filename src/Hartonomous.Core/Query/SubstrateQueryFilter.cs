using System.Collections.Generic;

namespace Hartonomous.Core.Query;

/// <summary>
/// Composable filter for substrate queries. Each non-null property narrows
/// the result set. Used by <see cref="ISubstrateQuery"/> for filtered
/// recomposer reads (per architecture.md "Distillation = WHERE clause").
///
/// All filters AND together. Within an array property (e.g., EntityTypeCodes)
/// values OR together. Null/empty means "no filter on that dimension."
/// </summary>
public sealed record SubstrateQueryFilter
{
    /// <summary>Restrict to entities of these type codes (e.g. ["tensor", "svd_rank_component"]).</summary>
    public IReadOnlyList<string>? EntityTypeCodes { get; init; }

    /// <summary>Restrict to entities/edges contributed by these model_source ids (e.g. all Qwen-Coder model sources).</summary>
    public IReadOnlyList<long>? ModelSourceIds { get; init; }

    /// <summary>Minimum significance mu in the given context (inclusive). Default: no minimum.</summary>
    public double? MinSignificanceMu { get; init; }

    /// <summary>Significance arena context to filter by (e.g. "model_trust", "semantic_relevance").</summary>
    public string? ContextTypeCode { get; init; }

    /// <summary>Maximum number of results. Default: no limit.</summary>
    public int? Limit { get; init; }
}
