using System;
using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Decomposers;

/// <summary>
/// The substrate's per-edge_type → arena routing table. Every corpus
/// decomposer call to <see cref="IIngestionBatch.AddEdge"/> MUST pass
/// the <see cref="EdgeRatingEvent"/>[] returned by
/// <see cref="EventsFor(string, double)"/> so the per-arena Glicko-2
/// surface accumulates evidence per AP-38 + the unified-Glicko-surface
/// design.
///
/// The routing maps each edge_type to the arena(s) that should receive
/// a Glicko event when this edge fires. Every event is sign-bearing per
/// AP-31; the corpus decomposers attest existence (positive_evidence)
/// unless the edge_type carries explicit refutation semantics
/// (e.g. <c>antonym</c> still fires positive_evidence — the antonym
/// relation EXISTS; the synthesizer interprets the pair's spectral
/// position via the antonym edge_type, not via negative-evidence Glicko).
///
/// Two universal arenas fire on every edge:
///   <c>source_authority</c>     — the source's trust prior carries here
///   <c>corroboration_strength</c> — cross-source agreement (mu drifts up
///                                    when the same edge identity is
///                                    re-observed from a new provenance)
///
/// Domain arenas fire per edge semantic:
///   POS classification, sense disambiguation     → lexical_disambiguation
///   semantic relations, gloss bridges            → semantic_relevance
///   UD deprel patterns                           → syntactic_role_fitness
///   translations (Wiktionary, Tatoeba, OMW)      → translation_quality
///   inflection / derivation / lemma / has_form   → morphological_productivity
///   has_gloss / has_example / has_pronunciation  → frequency_significance
///                                                   (entity occurrence
///                                                   accumulates here)
/// </summary>
public static class EdgeArenaRouter
{
    public const double DefaultWeight = 1.0;

    /// <summary>
    /// Return the canonical event array for an edge of the given type.
    /// The decomposer call site passes this directly into AddEdge's
    /// events parameter:
    ///   <c>batch.AddEdge(edgeType, prov, members, sig, EdgeArenaRouter.EventsFor(edgeType))</c>
    /// </summary>
    public static EdgeRatingEvent[] EventsFor(string edgeTypeCode, double weight = DefaultWeight)
    {
        if (!_edgeArenaMap.TryGetValue(edgeTypeCode, out string[]? arenas))
        {
            arenas = DefaultUniversalArenas;
        }

        EdgeRatingEvent[] events = new EdgeRatingEvent[arenas.Length];
        for (int i = 0; i < arenas.Length; i++)
        {
            events[i] = new EdgeRatingEvent(
                ContextTypeCode: arenas[i],
                AttestationTypeCode: "positive_evidence",
                Score: 1.0,
                Weight: weight);
        }
        return events;
    }

    /// <summary>
    /// As <see cref="EventsFor(string, double)"/> but the caller selects
    /// the sign explicitly (e.g. an explicit refutation observation).
    /// </summary>
    public static EdgeRatingEvent[] SignedEventsFor(
        string edgeTypeCode, double signedValue, double? weightOverride = null)
    {
        double w = weightOverride ?? Math.Abs(signedValue);
        if (w < 1e-9)
        {
            return Array.Empty<EdgeRatingEvent>();
        }

        bool positive = signedValue >= 0;
        string attestation = positive ? "positive_evidence" : "negative_evidence";
        double score = positive ? 1.0 : 0.0;

        if (!_edgeArenaMap.TryGetValue(edgeTypeCode, out string[]? arenas))
        {
            arenas = DefaultUniversalArenas;
        }

        EdgeRatingEvent[] events = new EdgeRatingEvent[arenas.Length];
        for (int i = 0; i < arenas.Length; i++)
        {
            events[i] = new EdgeRatingEvent(
                ContextTypeCode: arenas[i],
                AttestationTypeCode: attestation,
                Score: score,
                Weight: w);
        }
        return events;
    }

    public static IReadOnlyList<string> ArenasFor(string edgeTypeCode)
    {
        if (_edgeArenaMap.TryGetValue(edgeTypeCode, out string[]? arenas))
        {
            return arenas;
        }
        return DefaultUniversalArenas;
    }

    /// <summary>
    /// Generic-edge overload: returns the rating-event array for
    /// <paramref name="edgeTypeCode"/> attesting against a target entity of
    /// <paramref name="targetEntityTypeCode"/>. The target's entity_type is
    /// the dimension discriminator under the AP-30 / AP-38 collapse principle
    /// (one generic edge, dimensions distinguished by target type + provenance
    /// × arena). Falls back to the un-typed <see cref="EventsFor(string, double)"/>
    /// when no target-type-specific routing exists.
    /// </summary>
    public static EdgeRatingEvent[] EventsFor(
        string edgeTypeCode, string targetEntityTypeCode, double weight = DefaultWeight)
    {
        if (_genericEdgeTargetArenas.TryGetValue(
                (edgeTypeCode, targetEntityTypeCode), out string[]? arenas))
        {
            EdgeRatingEvent[] events = new EdgeRatingEvent[arenas.Length];
            for (int i = 0; i < arenas.Length; i++)
            {
                events[i] = new EdgeRatingEvent(
                    ContextTypeCode: arenas[i],
                    AttestationTypeCode: "positive_evidence",
                    Score: 1.0,
                    Weight: weight);
            }
            return events;
        }
        return EventsFor(edgeTypeCode, weight);
    }

    /// <summary>
    /// The source_authority + corroboration_strength baseline that EVERY
    /// edge fires. Domain arenas are added on top per edge_type via the
    /// route table. Declared before the routing dictionaries because their
    /// field initializers call <see cref="Universal"/> which reads this
    /// array — C# field-init order is declaration order, and a later
    /// declaration would NRE inside cctor.
    /// </summary>
    private static readonly string[] DefaultUniversalArenas =
    {
        "source_authority",
        "corroboration_strength",
    };

    private static string[] Universal(params string[] domain)
    {
        string[] result = new string[DefaultUniversalArenas.Length + domain.Length];
        Array.Copy(DefaultUniversalArenas, result, DefaultUniversalArenas.Length);
        Array.Copy(domain, 0, result, DefaultUniversalArenas.Length, domain.Length);
        return result;
    }

    /// <summary>
    /// Routing matrix for generic-edge (edge_type × target_entity_type) →
    /// arenas. The collapsed generic-edge surface (has_classification /
    /// has_relation / has_pattern_attestation / has_attribute) keys arena
    /// selection on the target's structural-kind entity_type instead of
    /// inventing per-dimension edge_type rows.
    /// </summary>
    private static readonly Dictionary<(string Edge, string TargetType), string[]>
        _genericEdgeTargetArenas = new()
    {
        // has_classification — codepoint-tier UCD properties
        [("has_classification", "general_category")]  = Universal("unicode_version_consensus"),
        [("has_classification", "script")]            = Universal("unicode_version_consensus", "script_membership_consensus"),
        [("has_classification", "block")]             = Universal("unicode_version_consensus"),
        [("has_classification", "bidi_class")]        = Universal("unicode_version_consensus"),
        [("has_classification", "east_asian_width")]  = Universal("unicode_version_consensus"),
        [("has_classification", "break_property")]    = Universal("unicode_version_consensus"),
        // has_classification — text-tier corpus classifications (mirrors what
        // legacy has_pos / has_lexname / has_morph_feature / has_deprel_pattern
        // route to, ready for migration off those per-dimension edge types)
        [("has_classification", "pos")]               = Universal("lexical_disambiguation", "syntactic_role_fitness"),
        [("has_classification", "lexname")]           = Universal("semantic_relevance"),
        [("has_classification", "morph_feature")]     = Universal("morphological_productivity"),
        [("has_classification", "deprel")]            = Universal("syntactic_role_fitness"),
        [("has_classification", "sense")]             = Universal("lexical_disambiguation", "semantic_relevance"),
        [("has_classification", "language_name")]     = Universal("language_codepoint_coverage_consensus"),
    };

    /// <summary>
    /// True iff <paramref name="edgeTypeCode"/> has an explicit routing entry
    /// in the <see cref="_edgeArenaMap"/>. Used by tests to verify the seed
    /// edge_type vocabulary is fully covered. Edges absent from the map fall
    /// back to <see cref="DefaultUniversalArenas"/> at runtime (no NRE), but
    /// auditors prefer explicit routing for documented arena attribution.
    /// </summary>
    public static bool IsExplicitlyRouted(string edgeTypeCode) =>
        _edgeArenaMap.ContainsKey(edgeTypeCode);

    /// <summary>
    /// Enumerate every edge_type code that has an explicit routing entry.
    /// </summary>
    public static IEnumerable<string> RoutedEdgeTypeCodes => _edgeArenaMap.Keys;

    private static readonly Dictionary<string, string[]> _edgeArenaMap = new(StringComparer.Ordinal)
    {
        // Lexical / classification surface
        ["has_pos"]                  = Universal("lexical_disambiguation"),
        ["has_sense"]                = Universal("lexical_disambiguation", "semantic_relevance"),
        ["aligned_to_synset"]        = Universal("semantic_relevance"),
        ["has_lemma"]                = Universal("morphological_productivity", "lexical_disambiguation"),
        ["has_form"]                 = Universal("morphological_productivity"),
        ["inflection_of"]            = Universal("morphological_productivity"),
        ["derived"]                  = Universal("morphological_productivity"),
        ["derivationally_related"]   = Universal("morphological_productivity", "semantic_relevance"),
        ["has_morph_feature"]        = Universal("morphological_productivity"),
        ["has_hyphenation"]          = Universal("morphological_productivity"),

        // Semantic relations (WordNet / OMW / Wiktionary)
        ["synonym"]                  = Universal("semantic_relevance"),
        ["antonym"]                  = Universal("semantic_relevance"),
        ["hypernym"]                 = Universal("semantic_relevance"),
        ["hyponym"]                  = Universal("semantic_relevance"),
        ["meronym"]                  = Universal("semantic_relevance"),
        ["holonym"]                  = Universal("semantic_relevance"),
        ["entails"]                  = Universal("semantic_relevance"),
        ["causes"]                   = Universal("semantic_relevance"),
        ["similar_to"]               = Universal("semantic_relevance"),
        ["related"]                  = Universal("semantic_relevance"),
        ["see_also"]                 = Universal("semantic_relevance"),
        ["pertainym"]                = Universal("semantic_relevance"),
        ["attribute_of"]             = Universal("semantic_relevance"),
        ["instance_hypernym"]        = Universal("semantic_relevance"),
        ["instance_hyponym"]         = Universal("semantic_relevance"),
        ["domain_topic"]             = Universal("semantic_relevance"),
        ["domain_region"]            = Universal("semantic_relevance"),
        ["domain_usage"]             = Universal("semantic_relevance"),

        // Translation (Wiktionary translations, Tatoeba sentence pairs, OMW alignments)
        ["translation_of"]           = Universal("translation_quality", "semantic_relevance"),
        ["translation_link"]         = Universal("translation_quality"),
        ["recording_of"]             = Universal("translation_quality"),

        // Etymology
        ["etym_derived_from"]        = Universal("frequency_significance"),
        ["etym_cognate_with"]        = Universal("frequency_significance", "semantic_relevance"),
        ["etym_inherited_from"]      = Universal("frequency_significance"),
        ["etym_borrowed_from"]       = Universal("frequency_significance"),
        ["etym_compound_of"]         = Universal("frequency_significance", "morphological_productivity"),
        ["has_etymology"]            = Universal("frequency_significance"),

        // Universal Dependencies — every UD deprel
        ["nsubj"]                    = Universal("syntactic_role_fitness"),
        ["nsubj:pass"]               = Universal("syntactic_role_fitness"),
        ["obj"]                      = Universal("syntactic_role_fitness"),
        ["iobj"]                     = Universal("syntactic_role_fitness"),
        ["csubj"]                    = Universal("syntactic_role_fitness"),
        ["ccomp"]                    = Universal("syntactic_role_fitness"),
        ["xcomp"]                    = Universal("syntactic_role_fitness"),
        ["obl"]                      = Universal("syntactic_role_fitness"),
        ["obl:agent"]                = Universal("syntactic_role_fitness"),
        ["obl:tmod"]                 = Universal("syntactic_role_fitness"),
        ["vocative"]                 = Universal("syntactic_role_fitness"),
        ["expl"]                     = Universal("syntactic_role_fitness"),
        ["dislocated"]               = Universal("syntactic_role_fitness"),
        ["advcl"]                    = Universal("syntactic_role_fitness"),
        ["advmod"]                   = Universal("syntactic_role_fitness"),
        ["discourse"]                = Universal("syntactic_role_fitness"),
        ["aux"]                      = Universal("syntactic_role_fitness"),
        ["cop"]                      = Universal("syntactic_role_fitness"),
        ["mark"]                     = Universal("syntactic_role_fitness"),
        ["nmod"]                     = Universal("syntactic_role_fitness"),
        ["nmod:poss"]                = Universal("syntactic_role_fitness"),
        ["nmod:tmod"]                = Universal("syntactic_role_fitness"),
        ["amod"]                     = Universal("syntactic_role_fitness"),
        ["appos"]                    = Universal("syntactic_role_fitness"),
        ["nummod"]                   = Universal("syntactic_role_fitness"),
        ["acl"]                      = Universal("syntactic_role_fitness"),
        ["acl:relcl"]                = Universal("syntactic_role_fitness"),
        ["det"]                      = Universal("syntactic_role_fitness"),
        ["det:predet"]               = Universal("syntactic_role_fitness"),
        ["clf"]                      = Universal("syntactic_role_fitness"),
        ["case"]                     = Universal("syntactic_role_fitness"),
        ["conj"]                     = Universal("syntactic_role_fitness"),
        ["cc"]                       = Universal("syntactic_role_fitness"),
        ["fixed"]                    = Universal("syntactic_role_fitness"),
        ["flat"]                     = Universal("syntactic_role_fitness"),
        ["compound"]                 = Universal("syntactic_role_fitness", "morphological_productivity"),
        ["compound:prt"]             = Universal("syntactic_role_fitness", "morphological_productivity"),
        ["list"]                     = Universal("syntactic_role_fitness"),
        ["parataxis"]                = Universal("syntactic_role_fitness"),
        ["orphan"]                   = Universal("syntactic_role_fitness"),
        ["goeswith"]                 = Universal("syntactic_role_fitness"),
        ["reparandum"]               = Universal("syntactic_role_fitness"),
        ["punct"]                    = Universal("syntactic_role_fitness"),
        ["dep"]                      = Universal("syntactic_role_fitness"),
        ["root"]                     = Universal("syntactic_role_fitness"),

        // Content / definition bridges
        ["has_gloss"]                = Universal("frequency_significance", "semantic_relevance"),
        ["has_example"]              = Universal("frequency_significance"),
        ["has_pronunciation"]        = Universal("frequency_significance"),
        ["has_definition"]           = Universal("frequency_significance", "semantic_relevance"),
        ["has_usage_note"]           = Universal("frequency_significance"),
        ["has_topic"]                = Universal("semantic_relevance"),

        // Language / script identity
        ["has_language"]             = Universal("source_authority"),
        ["has_script"]               = Universal("source_authority"),
        ["macrolanguage_contains"]   = Universal("source_authority"),
        ["superseded_by"]            = Universal("source_authority"),
        ["has_alternate_name"]       = Universal("source_authority"),

        // Unicode codepoint surface
        ["canonical_decomposition"]    = Universal("source_authority"),
        ["compatibility_decomposition"]= Universal("source_authority"),
        ["canonical_composes_to"]      = Universal("source_authority"),
        ["uppercase_mapping"]          = Universal("source_authority"),
        ["lowercase_mapping"]          = Universal("source_authority"),
        ["titlecase_mapping"]          = Universal("source_authority"),
        ["case_folding"]               = Universal("source_authority"),
        ["has_radical_stroke"]         = Universal("source_authority"),
        ["has_named_sequence"]         = Universal("source_authority"),
        ["has_emoji_sequence"]         = Universal("source_authority"),
        ["has_zwj_emoji_sequence"]     = Universal("source_authority"),
        ["has_standardized_variant"]   = Universal("source_authority"),
        ["confusable_with"]            = Universal("source_authority"),
        ["has_full_case_mapping"]      = Universal("source_authority"),
        ["has_ideographic_variant"]    = Universal("source_authority"),
        ["unihan_reading"]             = Universal("source_authority", "translation_quality"),
        ["idna_maps_to"]               = Universal("source_authority"),
        ["has_bidi_mirroring_glyph"]   = Universal("source_authority"),

        // Generic has_classification edge (Gate 1 #38 refactor 2026-05-19 —
        // collapsing per-dimension classification edges into one polymorphic
        // kind discriminated by target entity_type). Universal arena set;
        // domain arenas are added via EventsFor(edgeType, targetEntityType)
        // overload (_classificationTargetArenas map below).
        ["has_classification"]         = Universal(),

        // Provenance / audit
        ["has_source"]               = Universal("source_authority"),
        ["in_model"]                 = Universal("source_authority"),

        // Sequence-following bigram (Build-a-bear next-token prior)
        ["often_follows"]            = Universal("sequence_following", "frequency_significance"),

        // ── Gate 1 Reopening item #39 — orphan edge_type routings ─────────
        // Routes for edge_type seed codes (sql/schema/seed/edge_type.sql)
        // that previously fell back to universal arenas only. Each target
        // arena is verified to exist in sql/schema/seed/significance_context.sql
        // (19 seeded arenas). Listed alphabetically by edge_type code.

        // WordNet pointer: weak relatedness link between synsets.
        ["also_see"]                 = Universal("semantic_relevance"),
        // WordNet pointer: adjective synset ↔ noun synset attribute pairing.
        ["attribute"]                = Universal("semantic_relevance"),
        // Unicode case folding map (UCD CaseFolding.txt). Pure reference
        // metadata — universal source_authority is sufficient; listed for
        // explicit documentation per task #39.
        ["case_folds_to"]            = DefaultUniversalArenas,
        // WordNet pointer: causal entailment between verb synsets.
        ["cause"]                    = Universal("semantic_relevance"),
        // Polymorphic corpus window: word_form↔word_form within a sentence
        // window. Feeds sequence_following + frequency_significance.
        ["co_occurrence"]            = Universal("sequence_following", "frequency_significance"),
        // WordNet pointer: lemmas sharing the same direct hypernym.
        ["coordinate_term"]          = Universal("semantic_relevance"),
        // Tokenizer vocabulary coverage edge: word_form → lemma the tokenizer
        // covers via its vocab. Bridges morphology and disambiguation.
        ["covers_lemma"]             = Universal("morphological_productivity", "lexical_disambiguation"),
        // WordNet domain pointers (region / topic / usage). Semantic-domain
        // membership of synsets.
        ["domain_of_synset_region"]  = Universal("semantic_relevance"),
        ["domain_of_synset_topic"]   = Universal("semantic_relevance"),
        ["domain_of_synset_usage"]   = Universal("semantic_relevance"),
        // WordNet pointer: verb entailment between synsets.
        ["entailment"]               = Universal("semantic_relevance"),
        // Etymology relations from Wiktionary etymtree.
        ["etym_calque_of"]           = Universal("frequency_significance"),
        ["etym_etymon"]              = Universal("frequency_significance"),
        ["etym_link"]                = Universal("frequency_significance"),
        ["etym_mention"]             = Universal("frequency_significance"),
        // Unicode collation weight (DUCET / CLDR). Pure reference metadata —
        // universal source_authority is sufficient; listed for explicit
        // documentation per task #39.
        ["has_collation_weight"]     = DefaultUniversalArenas,
        // Structural: a word_form attested as a lexicalized compound of
        // smaller word_forms (e.g. "highrise"). Drives idiomaticity geometry.
        ["lexicalized_compound"]     = Universal("morphological_productivity", "semantic_relevance"),

        // ── Gate 5 prerequisites — model attestation edges ────────────────
        // Per docs/01-tensor-primitive-spec.md §IV, model decomposers emit
        // these typed attestation edges between content entities. Each
        // attestation participates in (provenance × arena) Glicko events.

        // word_form ↔ word_form attention head pattern attestation. Routes
        // through attention_pattern_confidence so cross-model attention
        // archetype consensus accumulates on its own arena, plus
        // semantic_relevance because attention patterns surface meaningful
        // token-to-token relationships.
        ["model_attention_pattern"]  = Universal("attention_pattern_confidence", "semantic_relevance"),
        // word_form ↔ word_form embedding-space cosine attestation. The
        // model's vote on which content entities are conceptually close.
        ["model_concept_similarity"] = Universal("semantic_relevance", "model_trust"),
        // word_form ↔ word_form FFN factor activation attestation. The
        // model's KV-memory binding between tokens.
        ["model_ffn_factor"]         = Universal("semantic_relevance", "model_trust"),
        // Cross-modal alignment (word_form ↔ pixel_region, word_form ↔
        // audio_chunk, decoder-token ↔ encoder-token, etc.). Translation
        // quality is the closest existing arena for cross-modal grounding;
        // semantic_relevance captures the meaning bridge.
        ["model_cross_modal_pattern"]= Universal("translation_quality", "semantic_relevance"),
        // Spatial pattern attestation (pixel_region ↔ pixel_region or
        // audio_chunk ↔ audio_chunk). Visual-domain arena deferred to
        // Gate 6 — frequency_significance is the closest existing arena
        // for spatial co-occurrence.
        ["model_spatial_pattern"]    = Universal("frequency_significance"),
        // Detection-head class attestation (object_query → visual_concept).
        // The model's vote on which class label binds to which detection
        // slot.
        ["model_detection_class"]    = Universal("semantic_relevance", "model_trust"),

        // ── Completion routings for remaining seeded edge_type codes ──────
        // Routed alongside task #39 to give every seeded edge_type explicit
        // arena attribution. Listed in seed-file order.

        // Structural details
        ["has_morpheme"]             = Universal("morphological_productivity"),
        ["has_name"]                 = DefaultUniversalArenas,
        ["has_wikidata"]             = DefaultUniversalArenas,
        ["has_frame"]                = Universal("syntactic_role_fitness"),
        ["has_contributor"]          = DefaultUniversalArenas,

        // Unicode case maps (UCD UnicodeData / SpecialCasing)
        ["maps_to_lowercase"]        = DefaultUniversalArenas,
        ["maps_to_uppercase"]        = DefaultUniversalArenas,
        ["maps_to_titlecase"]        = DefaultUniversalArenas,
        // Unicode decompositions (NormalizationProps / UnicodeData)
        ["has_canonical_decomposition"]     = DefaultUniversalArenas,
        ["has_compatibility_decomposition"] = DefaultUniversalArenas,
        // Unicode ZWJ-emoji sequences (emoji-data)
        ["has_emoji_zwj_sequence"]   = DefaultUniversalArenas,
        // Unicode Han ideographic variants + sources
        ["unihan_variant"]           = Universal("ivd_collection_consensus"),
        ["unihan_source"]            = DefaultUniversalArenas,
        // Encoding position consensus (ASCII / ISO 8859 / EBCDIC / etc.)
        ["has_encoding_position"]    = Universal("encoding_position_consensus"),
        // IVD collection ideographic-variant attestation
        ["has_ideographic_variant_in_collection"] = Universal("ivd_collection_consensus"),

        // Model-derived architecture metadata. These are model_architecture
        // and tensor metadata edges; they don't carry domain-evaluation
        // signal — they bind a model's structural facts to text content.
        ["in_layer"]                 = Universal("model_trust"),
        ["has_dtype"]                = Universal("model_trust"),
        ["has_shape"]                = Universal("model_trust"),
        ["has_hidden_size"]          = Universal("model_trust"),
        ["has_num_layers"]           = Universal("model_trust"),
        ["has_num_attention_heads"]  = Universal("model_trust"),
        ["has_vocab_size"]           = Universal("model_trust"),
        ["has_token_id"]             = Universal("model_trust"),
        ["in_vocabulary"]            = Universal("model_trust"),
        ["has_tensor"]               = Universal("model_trust"),
        ["has_architecture_name"]    = Universal("model_trust"),
        ["has_tensor_name"]          = Universal("model_trust"),
        // model_package_tensor analytics edges (per docs/01-tensor-primitive-spec.md)
        ["has_package_tensor_primitive"]        = Universal("model_trust"),
        ["has_package_tensor_tuple"]            = Universal("model_trust"),
        ["has_package_tensor_slot"]             = Universal("model_trust"),
        ["has_package_tensor_layer_index"]      = Universal("model_trust"),
        ["has_package_tensor_head_index"]       = Universal("model_trust"),
        ["has_package_tensor_expert_index"]     = Universal("model_trust"),
        ["has_package_tensor_modality"]         = Universal("model_trust"),
        ["has_package_tensor_fused_slice"]      = Universal("model_trust"),
        ["has_package_tensor_linearized_shape"] = Universal("model_trust"),
        ["has_tokenizer_model"]      = Universal("model_trust"),
        ["has_token_in_tokenizer"]   = Universal("model_trust"),
        // Model-package text artifacts (config / tokenizer / readme / etc.)
        ["has_config_artifact"]            = Universal("model_trust"),
        ["has_tokenizer_artifact"]         = Universal("model_trust"),
        ["has_tokenizer_config_artifact"]  = Universal("model_trust"),
        ["has_special_tokens_artifact"]    = Universal("model_trust"),
        ["has_merges_artifact"]            = Universal("model_trust"),
        ["has_chat_template_artifact"]     = Universal("model_trust"),
        ["has_generation_config_artifact"] = Universal("model_trust"),
        ["has_readme_artifact"]            = Universal("model_trust"),

        // WordNet meronym / holonym subtype detail. The router previously
        // had generic `meronym` / `holonym` collapsed entries; the seed
        // distinguishes member / substance / part variants and every variant
        // attests on semantic_relevance.
        ["member_holonym"]           = Universal("semantic_relevance"),
        ["substance_holonym"]        = Universal("semantic_relevance"),
        ["part_holonym"]             = Universal("semantic_relevance"),
        ["member_meronym"]           = Universal("semantic_relevance"),
        ["substance_meronym"]        = Universal("semantic_relevance"),
        ["part_meronym"]             = Universal("semantic_relevance"),
        // WordNet verb_group / participle_of_verb / domain-membership pointers
        ["verb_group"]               = Universal("semantic_relevance"),
        ["participle_of_verb"]       = Universal("semantic_relevance", "morphological_productivity"),
        ["member_of_domain_topic"]   = Universal("semantic_relevance"),
        ["member_of_domain_region"]  = Universal("semantic_relevance"),
        ["member_of_domain_usage"]   = Universal("semantic_relevance"),

        // ISO 639 / region cross-link edges. Identity-binding metadata that
        // fires on the language-identity attestation arenas.
        ["has_iso_639_1_code"]       = Universal("script_membership_consensus"),
        ["has_iso_639_2b_code"]      = Universal("script_membership_consensus"),
        ["has_iso_639_2t_code"]      = Universal("script_membership_consensus"),
        ["has_region"]                = DefaultUniversalArenas,

        // AP-8 unified Glicko surface — deprel / lexname classification edges
        ["has_deprel_pattern"]       = Universal("syntactic_role_fitness"),
        ["has_lexname"]              = Universal("lexical_disambiguation", "semantic_relevance"),
    };
}
