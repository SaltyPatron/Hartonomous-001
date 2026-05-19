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
    public const string InferTopK   = "substrate.infer_topk";
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

    public const string ApplyFireflyRotation = "substrate.apply_firefly_rotation";
    public const string BindBpeTokensToSeedMorph = "substrate.bind_bpe_tokens_to_seed_morph";
    public const string BindBpeTokensToSeedPos = "substrate.bind_bpe_tokens_to_seed_pos";
    public const string BreakPropertyCodeMap = "substrate.break_property_code_map";
    public const string BreakPropertyFullMap = "substrate.break_property_full_map";
    public const string ClaimOrGetEmbeddingAnchor = "substrate.claim_or_get_embedding_anchor";
    public const string CodepointPropertyRows = "substrate.codepoint_property_rows";
    public const string CompositionRange = "substrate.composition_range";
    public const string EmbeddingFireflyTokenHashes = "substrate.embedding_firefly_token_hashes";
    public const string GetFireflyCoords = "substrate.get_firefly_coords";
    public const string GetEdgeInfoByHandles = "substrate.get_edge_info_by_handles";
    public const string GetEntityInfoByHandles = "substrate.get_entity_info_by_handles";
    public const string GetOutboundEdgeTargets = "substrate.get_outbound_edge_targets";
    public const string GetCompletedModelPasses = "substrate.get_completed_model_passes";
    public const string HealthSummary = "substrate.health_summary";
    public const string ModelInventory = "substrate.model_inventory";
    public const string ModelVocabRecovered = "substrate.model_vocab_recovered";
    public const string PhysicalityLineString4d = "substrate.physicality_linestring4d";
    public const string PhysicalityPoint4d = "substrate.physicality_point4d";
    public const string PopulateBlocks = "substrate.populate_blocks";
    public const string PopulateBreakProperties = "substrate.populate_break_properties";
    public const string PopulateDeprels = "substrate.populate_deprels";
    public const string PopulateGeneralCategories = "substrate.populate_general_categories";
    public const string PopulateLanguages = "substrate.populate_languages";
    public const string PopulateMorphFeatures = "substrate.populate_morph_features";
    public const string PromptDocumentReady = "substrate.prompt_document_ready";
    public const string PopulateScripts = "substrate.populate_scripts";
    public const string PreviewTargetArch = "substrate.preview_target_arch";
    public const string QueryAttentionComponents = "substrate.query_attention_components";
    public const string QueryEntities = "substrate.query_entities";
    public const string QueryFfnNeuronsByHiddenDim = "substrate.query_ffn_neurons_by_hidden_dim";
    public const string QueryFirefliesForVocab = "substrate.query_fireflies_for_vocab";
    public const string QuerySingularDirectionsForRole = "substrate.query_singular_directions_for_role";
    public const string QueryTensorsForArchitecture = "substrate.query_tensors_for_architecture";
    public const string QueryTensorsForModelSource = "substrate.query_tensors_for_model_source";
    public const string ReferenceCodeDoubleMap = "substrate.reference_code_double_map";
    public const string ReferenceCodeMap = "substrate.reference_code_map";
    public const string ReferenceCodeTextMap = "substrate.reference_code_text_map";
    public const string ReferenceIdByCode = "substrate.reference_id_by_code";
    public const string ReferenceInt64Set = "substrate.reference_int64_set";
    public const string ReferenceKeyValueMap = "substrate.reference_key_value_map";
    public const string ReferenceLanguageAliasMap = "substrate.reference_language_alias_map";
    public const string RecomposeAuditWalk = "substrate.recompose_audit_walk";
    public const string RecordAttestationsBulk = "substrate.record_attestations_bulk";
    public const string RecordOutcome = "substrate.record_outcome";
    public const string RecordOutcomesBulk = "substrate.record_outcomes_bulk";
    public const string RecordEdgeComparison = "substrate.record_edge_comparison";
    public const string RecordEntityComparison = "substrate.record_entity_comparison";
    public const string InitializeEdgeSignificance = "substrate.initialize_edge_significance";
    public const string InitializeEntitySignificance = "substrate.initialize_entity_significance";
    public const string PruneSignificanceForContext = "substrate.prune_significance_for_context";
    public const string Recall = "substrate.recall";
    public const string RefinementSummary = "substrate.refinement_summary";
    public const string RefinementSummaryTop = "substrate.refinement_summary_top";
    public const string ResolveEntityHandles = "substrate.resolve_entity_handles";
    public const string SignificanceContextIds = "substrate.significance_context_ids";
    public const string TraversalNeighbors = "substrate.traversal_neighbors";
    public const string UcdMaterializationCounts = "substrate.ucd_materialization_counts";
    public const string UcdReferenceVocabularyCounts = "substrate.ucd_reference_vocabulary_counts";
    public const string UcdVersion = "substrate.ucd_version";
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
        InferTopK,
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
        ApplyFireflyRotation,
        BindBpeTokensToSeedMorph,
        BindBpeTokensToSeedPos,
        BreakPropertyCodeMap,
        BreakPropertyFullMap,
        ClaimOrGetEmbeddingAnchor,
        CodepointPropertyRows,
        CompositionRange,
        EmbeddingFireflyTokenHashes,
        GetFireflyCoords,
        GetEdgeInfoByHandles,
        GetEntityInfoByHandles,
        GetOutboundEdgeTargets,
        GetCompletedModelPasses,
        HealthSummary,
        ModelInventory,
        ModelVocabRecovered,
        PhysicalityLineString4d,
        PhysicalityPoint4d,
        PopulateBlocks,
        PopulateBreakProperties,
        PopulateDeprels,
        PopulateGeneralCategories,
        PopulateLanguages,
        PopulateMorphFeatures,
        PromptDocumentReady,
        PopulateScripts,
        PreviewTargetArch,
        QueryAttentionComponents,
        QueryEntities,
        QueryFfnNeuronsByHiddenDim,
        QueryFirefliesForVocab,
        QuerySingularDirectionsForRole,
        QueryTensorsForArchitecture,
        QueryTensorsForModelSource,
        ReferenceCodeDoubleMap,
        ReferenceCodeMap,
        ReferenceCodeTextMap,
        ReferenceIdByCode,
        ReferenceInt64Set,
        ReferenceKeyValueMap,
        ReferenceLanguageAliasMap,
        RecomposeAuditWalk,
        RecordAttestationsBulk,
        RecordOutcome,
        RecordOutcomesBulk,
        RecordEdgeComparison,
        RecordEntityComparison,
        InitializeEdgeSignificance,
        InitializeEntitySignificance,
        PruneSignificanceForContext,
        Recall,
        RefinementSummary,
        RefinementSummaryTop,
        ResolveEntityHandles,
        SignificanceContextIds,
        TraversalNeighbors,
        UcdMaterializationCounts,
        UcdReferenceVocabularyCounts,
        UcdVersion,
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
