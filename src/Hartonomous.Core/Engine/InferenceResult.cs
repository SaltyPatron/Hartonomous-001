using System;
using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

/// <summary>
/// Result of an inference query. Carries the recomposed answer text the
/// substrate's traversal produced, plus the diagnostic trace (seed entities,
/// paths, entities visited) so callers can inspect HOW the substrate
/// arrived at the answer — the explanation IS the path, per Substrate Law.
///
/// Hash-as-PK throughout. No long-id fields.
/// </summary>
public sealed record InferenceResult
{
    /// <summary>
    /// The substrate's recomposed answer to the prompt. Built by walking
    /// the highest-significance path the traversal found and concatenating
    /// the content of each entity along the path. Empty when traversal
    /// found no path (substrate has nothing to say — honest abstention,
    /// per the spec).
    /// </summary>
    public required string Answer { get; init; }

    /// <summary>The composite-handle seeds the prompt decomposed to.</summary>
    public required IReadOnlyList<EntityHandle> Seeds { get; init; }

    /// <summary>
    /// Every path the traversal returned, ordered by composite path
    /// significance (highest first). The first path is the one that
    /// produced <see cref="Answer"/>; the rest are runner-up alternatives
    /// the explanation trace can reference.
    /// </summary>
    public required IReadOnlyList<TraversalPath> Paths { get; init; }

    /// <summary>Entity metadata for every entity referenced in paths, keyed by handle.</summary>
    public required IReadOnlyDictionary<EntityHandle, EntityInfo> Entities { get; init; }

    /// <summary>Total substrate nodes visited during traversal across all arenas/targets.</summary>
    public int NodesVisited { get; init; }

    /// <summary>End-to-end elapsed time: prompt decomposition + traversal + recomposition.</summary>
    public TimeSpan Elapsed { get; init; }
}
