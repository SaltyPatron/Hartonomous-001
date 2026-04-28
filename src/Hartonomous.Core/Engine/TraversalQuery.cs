using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

/// <summary>
/// Internal traversal query. The inference engine (not the caller) decides
/// the values here — depth, target types, arenas — based on the substrate's
/// own state. Callers do NOT specify these knobs through the public
/// inference API; they are intra-engine implementation details.
///
/// Hash-as-PK: seeds are composite <see cref="EntityHandle"/>s.
/// </summary>
public sealed record TraversalQuery
{
    /// <summary>Composite-handle seeds for A* expansion.</summary>
    public required IReadOnlyList<EntityHandle> Seeds { get; init; }

    /// <summary>Engine-chosen depth bound. Defaults to a deep traversal (30).</summary>
    public int MaxDepth { get; init; } = 30;

    /// <summary>
    /// Engine-chosen significance gate. Default 0 = no gate; let the
    /// substrate's actual edge significance compose into path significance
    /// without a caller-imposed cutoff.
    /// </summary>
    public double SignificanceThreshold { get; init; }

    /// <summary>Engine-chosen cost budget. Default very large = effectively unbounded.</summary>
    public double CostBudget { get; init; } = double.PositiveInfinity;

    /// <summary>Engine-chosen edge-type narrowing. Default null = traverse every edge type.</summary>
    public IReadOnlyList<string>? EdgeTypeFilter { get; init; }

    /// <summary>
    /// Engine-chosen arena. Internal only — the inference engine fans out
    /// across all arenas and composes; this field carries the per-fan-out
    /// arena code, not a caller knob.
    /// </summary>
    public required string ArenaCode { get; init; }
}
