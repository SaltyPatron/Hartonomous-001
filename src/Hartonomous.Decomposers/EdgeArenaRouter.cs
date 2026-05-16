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
    /// The source_authority + corroboration_strength baseline that EVERY
    /// edge fires. Domain arenas are added on top per edge_type via the
    /// route table.
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

        // Provenance / audit
        ["has_source"]               = Universal("source_authority"),
        ["in_model"]                 = Universal("source_authority"),

        // Sequence-following bigram (Build-a-bear next-token prior)
        ["often_follows"]            = Universal("sequence_following", "frequency_significance"),
    };
}
