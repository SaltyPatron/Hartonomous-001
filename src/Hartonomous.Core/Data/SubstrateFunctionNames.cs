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

    public const string CompositionRange = "substrate.composition_range";
    public const string GetEdgeInfoByHandles = "substrate.get_edge_info_by_handles";
    public const string GetEntityInfoByHandles = "substrate.get_entity_info_by_handles";
    public const string GetOutboundEdgeTargets = "substrate.get_outbound_edge_targets";
    public const string GetCompletedModelPasses = "substrate.get_completed_model_passes";
    public const string LoadWordNetOffsetSynsetMap = "substrate.load_wordnet_offset_synset_map";
    public const string PhysicalityLineString4d = "substrate.physicality_linestring4d";
    public const string PhysicalityPoint4d = "substrate.physicality_point4d";
    public const string PopulateBlocks = "substrate.populate_blocks";
    public const string PopulateBreakProperties = "substrate.populate_break_properties";
    public const string PopulateDeprels = "substrate.populate_deprels";
    public const string PopulateGeneralCategories = "substrate.populate_general_categories";
    public const string PopulateLanguages = "substrate.populate_languages";
    public const string PopulateMorphFeatures = "substrate.populate_morph_features";
    public const string PopulateScripts = "substrate.populate_scripts";
    public const string PopulateSenses = "substrate.populate_senses";
    public const string ReferenceCodeDoubleMap = "substrate.reference_code_double_map";
    public const string ReferenceCodeMap = "substrate.reference_code_map";
    public const string ReferenceCodeTextMap = "substrate.reference_code_text_map";
    public const string ReferenceIdByCode = "substrate.reference_id_by_code";
    public const string ReferenceInt64Set = "substrate.reference_int64_set";
    public const string ReferenceKeyValueMap = "substrate.reference_key_value_map";
    public const string RecomposeText = "substrate.recompose_text";
    public const string ResolveEntityHandles = "substrate.resolve_entity_handles";
    public const string UpsertArchitectureClass = "substrate.upsert_architecture_class";
    public const string UpsertHomogeneousEdgeTypes = "substrate.upsert_homogeneous_edge_types";
    public const string UpsertModelPassCheckpoint = "substrate.upsert_model_pass_checkpoint";
    public const string UpsertModelPublisher = "substrate.upsert_model_publisher";
    public const string UpsertModelRegistry = "substrate.upsert_model_registry";
    public const string UpsertModelSource = "substrate.upsert_model_source";
    public const string UpsertReferenceEdgeType = "substrate.upsert_reference_edge_type";

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
        CompositionRange,
        GetEdgeInfoByHandles,
        GetEntityInfoByHandles,
        GetOutboundEdgeTargets,
        GetCompletedModelPasses,
        LoadWordNetOffsetSynsetMap,
        PhysicalityLineString4d,
        PhysicalityPoint4d,
        PopulateBlocks,
        PopulateBreakProperties,
        PopulateDeprels,
        PopulateGeneralCategories,
        PopulateLanguages,
        PopulateMorphFeatures,
        PopulateScripts,
        PopulateSenses,
        ReferenceCodeDoubleMap,
        ReferenceCodeMap,
        ReferenceCodeTextMap,
        ReferenceIdByCode,
        ReferenceInt64Set,
        ReferenceKeyValueMap,
        RecomposeText,
        ResolveEntityHandles,
        UpsertArchitectureClass,
        UpsertHomogeneousEdgeTypes,
        UpsertModelPassCheckpoint,
        UpsertModelPublisher,
        UpsertModelRegistry,
        UpsertModelSource,
        UpsertReferenceEdgeType,
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
