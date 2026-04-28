using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

/// <summary>
/// A query to the inference engine. Per the substrate-as-AI-model invention,
/// the prompt IS substrate content (decomposed via the same TextDecomposer
/// every other text source uses) and the forward pass IS A* traversal over
/// significance-weighted typed edges. The query carries the prompt text
/// only — no caller-specified arena, depth bound, cost budget, significance
/// threshold, edge-type filter, or result cap. Those are conventional-IR
/// shortcuts; the substrate's own significance state and termination
/// criteria drive the traversal.
///
/// Hash-as-PK: pre-resolved seeds (when supplied) are composite handles.
/// </summary>
public sealed record InferenceQuery
{
    /// <summary>The prompt. Decomposed by the engine into seed entities.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Pre-resolved composite-handle seeds. Only for callers that already
    /// have substrate handles and want to skip prompt decomposition (e.g.,
    /// recursive engine self-calls, integration tests).
    /// </summary>
    public IReadOnlyList<EntityHandle>? Seeds { get; init; }
}
