using System.Collections.Generic;

namespace Hartonomous.Core.Data;

/// <summary>
/// Allowlist of substrate function names callable through
/// <see cref="BaseSubstrateRepository"/>. Adding a new function call site =
/// adding a constant here AND a method on the appropriate concrete repository.
/// The base class verifies the name against <see cref="Allowlist"/> before
/// constructing SQL — this is the only place SQL strings are built, and the
/// only inputs are vetted constants.
/// </summary>
public static class SubstrateFunctionNames
{
    public const string Complete    = "substrate.complete";
    public const string Infer       = "substrate.infer";
    public const string Classify    = "substrate.classify";
    public const string Rerank      = "substrate.rerank";
    public const string EmbedLookup = "substrate.embed_lookup";

    public const string ApiEdgeByHash = "substrate.api_edge_by_hash";
    public const string ApiEntityByHash = "substrate.api_entity_by_hash";
    public const string ApiEntityClassifications = "substrate.api_entity_classifications";
    public const string ApiEntityEdges = "substrate.api_entity_edges";
    public const string ApiEntityNeighbors = "substrate.api_entity_neighbors";
    public const string ApiEntitySignificance = "substrate.api_entity_significance";
    public const string ApiListEntities = "substrate.api_list_entities";

    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(System.StringComparer.Ordinal)
    {
        Complete,
        Infer,
        Classify,
        Rerank,
        EmbedLookup,
        ApiEdgeByHash,
        ApiEntityByHash,
        ApiEntityClassifications,
        ApiEntityEdges,
        ApiEntityNeighbors,
        ApiEntitySignificance,
        ApiListEntities,
    };

    public static void AssertAllowlisted(string functionName)
    {
        if (!Allowlist.Contains(functionName))
        {
            throw new InvalidOperationException(
                $"Substrate function name '{functionName}' is not in the allowlist. " +
                "Add it to SubstrateFunctionNames.Allowlist before calling.");
        }
    }
}
