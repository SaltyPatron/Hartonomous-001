using System.Collections.Immutable;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// User-tunable knobs that bias Build-a-bear synthesis toward specific
/// arenas / provenances / personalities. Two synthesis runs with different
/// RecompositionOptions on the SAME substrate state produce DIFFERENT
/// models — no re-training, no re-ingestion. The substrate is the common
/// content base; these options are inference-time projection knobs.
///
/// Empty dictionaries / null arrays mean "use defaults" — equal-weight
/// blend across every arena currently in substrate.significance_context,
/// every provenance currently in substrate.provenance.
/// </summary>
public sealed record RecompositionOptions(
    ImmutableDictionary<string, double> ArenaWeights,
    ImmutableDictionary<string, double> ProvenanceWeights,
    ImmutableArray<string> ProvenanceAllowlist,
    ImmutableArray<string> ProvenanceBlocklist,
    QuantizationTarget OutputDtype,
    int LayerAssignmentSeed,
    double SignificanceFloor,
    int? EgonetHops,
    bool HonestAbstention,
    ImmutableArray<string> SeedConcepts = default,
    int KnowledgeBfsTopK = 32)
{
    public static RecompositionOptions Default { get; } = new(
        ArenaWeights: ImmutableDictionary<string, double>.Empty,
        ProvenanceWeights: ImmutableDictionary<string, double>.Empty,
        ProvenanceAllowlist: ImmutableArray<string>.Empty,
        ProvenanceBlocklist: ImmutableArray<string>.Empty,
        OutputDtype: QuantizationTarget.F32,
        LayerAssignmentSeed: 0,
        SignificanceFloor: 1500.0,
        EgonetHops: 2,
        HonestAbstention: true,
        SeedConcepts: ImmutableArray<string>.Empty,
        KnowledgeBfsTopK: 32);

    /// <summary>
    /// Distill what curated lexicons agree on. Ignores corpora, models,
    /// user sessions. Useful for "what does the encyclopedia say."
    /// </summary>
    public static RecompositionOptions Encyclopedic => Default with
    {
        ArenaWeights = new Dictionary<string, double>
        {
            ["lexical_disambiguation"] = 1.0,
            ["semantic_relevance"] = 1.0,
            ["morphological_productivity"] = 0.5,
            ["syntactic_role_fitness"] = 0.5,
        }.ToImmutableDictionary(),
        ProvenanceAllowlist = ImmutableArray.Create(
            "princeton_wordnet", "omwn_consortium", "wiktextract", "unicode_consortium", "library_of_congress"),
    };

    /// <summary>
    /// Tatoeba-flavored conversational. Weighted toward attested real-world
    /// usage rather than dictionary definitions.
    /// </summary>
    public static RecompositionOptions Conversational => Default with
    {
        ArenaWeights = new Dictionary<string, double>
        {
            ["syntactic_role_fitness"] = 1.0,
            ["frequency_significance"] = 1.0,
            ["semantic_relevance"] = 0.7,
            ["corroboration_strength"] = 0.5,
        }.ToImmutableDictionary(),
        ProvenanceAllowlist = ImmutableArray.Create(
            "tatoeba", "universaldependencies", "wiktextract"),
    };

    /// <summary>
    /// Practitioner-personality dominant. user_session/* provenance weighted
    /// high; other sources scale to their trust priors. Use after the
    /// practitioner has authored content into the substrate via the live
    /// ingest path.
    /// </summary>
    public static RecompositionOptions Practitioner => Default with
    {
        ProvenanceWeights = new Dictionary<string, double>
        {
            ["user_session"] = 10.0,
            ["princeton_wordnet"] = 1.0,
            ["wiktextract"] = 1.0,
            ["tatoeba"] = 1.0,
            ["universaldependencies"] = 1.0,
        }.ToImmutableDictionary(),
    };

    /// <summary>
    /// Grammar tutor: heavy weight on UD's syntactic distribution +
    /// WordNet's POS classifications + morphological productivity.
    /// </summary>
    public static RecompositionOptions GrammarTutor => Default with
    {
        ArenaWeights = new Dictionary<string, double>
        {
            ["syntactic_role_fitness"] = 1.0,
            ["morphological_productivity"] = 1.0,
            ["lexical_disambiguation"] = 0.5,
        }.ToImmutableDictionary(),
        ProvenanceAllowlist = ImmutableArray.Create(
            "universaldependencies", "princeton_wordnet", "wiktextract"),
    };
}

