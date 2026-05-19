using System.Collections.Generic;
using System.Linq;
using Hartonomous.Core.Ingestion;
using Hartonomous.Decomposers;

namespace Hartonomous.Decomposers.Tests.Routing;

/// <summary>
/// Verifies <see cref="EdgeArenaRouter"/> coverage against the canonical
/// <c>sql/schema/seed/edge_type.sql</c> + <c>sql/schema/seed/significance_context.sql</c>
/// seed vocabularies. This is a Gate 1 closure test for task #39 (orphan
/// edge_type routings).
///
/// The expected edge_type seed list and significance_context seed list are
/// embedded here per the testing rule "Synthetic data over file fixtures.
/// Generate XML, create temp files, test in isolation." Recompute from
/// <c>sql/schema/seed/edge_type.sql</c> when the seed changes.
/// </summary>
public sealed class EdgeArenaRouterTests
{
    /// <summary>
    /// Every edge_type code in <c>sql/schema/seed/edge_type.sql</c>.
    /// Recomputed 2026-05-18 (post-task #39 orphan routing addition).
    /// </summary>
    private static readonly HashSet<string> SeededEdgeTypes = new(System.StringComparer.Ordinal)
    {
        // structural
        "has_sense", "has_form", "has_lemma", "has_morpheme", "has_gloss",
        "has_example", "has_name", "inflection_of", "has_etymology",
        "has_pronunciation", "has_hyphenation", "has_wikidata",
        "lexicalized_compound", "has_frame",
        // cross_lingual
        "aligned_to_synset", "translation_of", "translation_link",
        "macrolanguage_contains", "has_alternate_name", "superseded_by",
        "etym_inherited_from", "etym_derived_from", "etym_borrowed_from",
        "etym_cognate_with", "etym_calque_of", "etym_mention", "etym_link",
        "etym_etymon",
        // cross_modal
        "recording_of", "has_contributor",
        // unicode
        "maps_to_lowercase", "case_folds_to", "has_collation_weight",
        "maps_to_uppercase", "maps_to_titlecase",
        "has_canonical_decomposition", "has_compatibility_decomposition",
        "canonical_composes_to", "has_full_case_mapping",
        "has_named_sequence", "has_standardized_variant",
        "has_emoji_sequence", "has_emoji_zwj_sequence", "confusable_with",
        "idna_maps_to", "has_bidi_mirroring_glyph", "unihan_variant",
        "unihan_reading", "unihan_source", "has_radical_stroke",
        "has_encoding_position", "has_ideographic_variant_in_collection",
        // model_derived
        "in_model", "in_layer", "has_dtype", "has_shape", "has_hidden_size",
        "has_num_layers", "has_num_attention_heads", "has_vocab_size",
        "has_token_id", "in_vocabulary", "has_tensor", "has_architecture_name",
        "has_tensor_name",
        "has_package_tensor_primitive", "has_package_tensor_tuple",
        "has_package_tensor_slot", "has_package_tensor_layer_index",
        "has_package_tensor_head_index", "has_package_tensor_expert_index",
        "has_package_tensor_modality", "has_package_tensor_fused_slice",
        "has_package_tensor_linearized_shape",
        "has_tokenizer_model", "has_token_in_tokenizer", "covers_lemma",
        "co_occurrence",
        "has_config_artifact", "has_tokenizer_artifact",
        "has_tokenizer_config_artifact", "has_special_tokens_artifact",
        "has_merges_artifact", "has_chat_template_artifact",
        "has_generation_config_artifact", "has_readme_artifact",
        "model_concept_similarity", "model_attention_pattern",
        "model_ffn_factor", "model_spatial_pattern",
        "model_cross_modal_pattern", "model_detection_class",
        // semantic
        "hypernym", "hyponym", "instance_hypernym", "instance_hyponym",
        "member_holonym", "substance_holonym", "part_holonym",
        "member_meronym", "substance_meronym", "part_meronym",
        "attribute", "derivationally_related", "antonym", "similar_to",
        "also_see", "verb_group", "entailment", "cause",
        "participle_of_verb", "pertainym",
        "domain_of_synset_topic", "member_of_domain_topic",
        "domain_of_synset_region", "member_of_domain_region",
        "domain_of_synset_usage", "member_of_domain_usage",
        "synonym", "coordinate_term", "derived", "related",
        // sequence
        "often_follows",
        // cross_lingual extensions
        "has_iso_639_1_code", "has_iso_639_2b_code", "has_iso_639_2t_code",
        "has_script", "has_region",
        // AP-8 unified Glicko surface
        "has_pos", "has_morph_feature", "has_deprel_pattern", "has_lexname",
        "has_language",
    };

    /// <summary>
    /// Every significance_context code in
    /// <c>sql/schema/seed/significance_context.sql</c>.
    /// Recomputed 2026-05-18.
    /// </summary>
    private static readonly HashSet<string> SeededSignificanceContexts = new(System.StringComparer.Ordinal)
    {
        "lexical_disambiguation", "syntactic_role_fitness", "translation_quality",
        "model_trust", "source_authority", "semantic_relevance",
        "corroboration_strength", "frequency_significance",
        "attention_pattern_confidence", "morphological_productivity",
        "sequence_following",
        "unicode_version_consensus", "encoding_position_consensus",
        "ivd_collection_consensus", "unihan_reading_consensus",
        "consortium_discussion_density", "script_membership_consensus",
        "language_codepoint_coverage_consensus", "locale_definition_consensus",
    };

    /// <summary>
    /// Edge types deliberately NOT covered by an explicit routing entry.
    /// Empty as of task #39 completion — every seeded edge_type now has
    /// either a domain-arena routing or an explicit
    /// <c>DefaultUniversalArenas</c> entry. Listed here so future omissions
    /// must be justified inline rather than left as silent fall-throughs.
    /// </summary>
    private static readonly HashSet<string> AcceptedUnrouted = new(System.StringComparer.Ordinal)
    {
        // Empty — all edge types now have explicit routing.
    };

    [Fact]
    public void Every_Task39_OrphanEdgeType_IsExplicitlyRouted()
    {
        // The exact 23-entry orphan list named in task #39.
        string[] orphans =
        {
            // Model edges (Gate 5 prerequisite)
            "model_attention_pattern", "model_concept_similarity",
            "model_ffn_factor", "model_cross_modal_pattern",
            "model_spatial_pattern", "model_detection_class",
            // Corpus orphans
            "also_see", "attribute", "case_folds_to", "cause",
            "co_occurrence", "coordinate_term", "covers_lemma",
            "domain_of_synset_region", "domain_of_synset_topic",
            "domain_of_synset_usage", "entailment",
            "etym_calque_of", "etym_etymon", "etym_link", "etym_mention",
            "has_collation_weight", "lexicalized_compound",
        };

        Assert.Equal(23, orphans.Length);
        foreach (string code in orphans)
        {
            Assert.True(
                EdgeArenaRouter.IsExplicitlyRouted(code),
                $"Task #39 orphan edge_type '{code}' is still falling back to universal arenas — add an explicit entry to EdgeArenaRouter._edgeArenaMap.");
        }
    }

    [Fact]
    public void Every_SeededEdgeType_IsRouted_OrInAcceptedUnroutedList()
    {
        List<string> orphaned = new();
        foreach (string code in SeededEdgeTypes)
        {
            if (!EdgeArenaRouter.IsExplicitlyRouted(code) && !AcceptedUnrouted.Contains(code))
            {
                orphaned.Add(code);
            }
        }

        Assert.True(
            orphaned.Count == 0,
            $"The following edge_type seed codes have no routing entry and are not in AcceptedUnrouted: {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void Every_RoutedArena_ExistsInSignificanceContextSeed()
    {
        List<string> bad = new();
        foreach (string edgeCode in EdgeArenaRouter.RoutedEdgeTypeCodes)
        {
            foreach (string arena in EdgeArenaRouter.ArenasFor(edgeCode))
            {
                if (!SeededSignificanceContexts.Contains(arena))
                {
                    bad.Add($"{edgeCode} -> {arena}");
                }
            }
        }

        Assert.True(
            bad.Count == 0,
            $"The following edge_type → arena routings reference arenas absent from significance_context.sql: {string.Join(", ", bad)}");
    }

    [Fact]
    public void EventsFor_OrphanRoute_FiresOnDomainArenas_NotUniversalOnly()
    {
        // Spot-check three critical task #39 entries to confirm domain
        // arenas are wired up beyond the universal fallback.

        EdgeRatingEvent[] attn = EdgeArenaRouter.EventsFor("model_attention_pattern");
        string[] attnArenas = attn.Select(e => e.ContextTypeCode).ToArray();
        Assert.Contains("attention_pattern_confidence", attnArenas);
        Assert.Contains("semantic_relevance", attnArenas);
        Assert.Contains("source_authority", attnArenas);
        Assert.Contains("corroboration_strength", attnArenas);

        EdgeRatingEvent[] coOcc = EdgeArenaRouter.EventsFor("co_occurrence");
        string[] coOccArenas = coOcc.Select(e => e.ContextTypeCode).ToArray();
        Assert.Contains("sequence_following", coOccArenas);
        Assert.Contains("frequency_significance", coOccArenas);

        EdgeRatingEvent[] lex = EdgeArenaRouter.EventsFor("lexicalized_compound");
        string[] lexArenas = lex.Select(e => e.ContextTypeCode).ToArray();
        Assert.Contains("morphological_productivity", lexArenas);
        Assert.Contains("semantic_relevance", lexArenas);
    }
}
