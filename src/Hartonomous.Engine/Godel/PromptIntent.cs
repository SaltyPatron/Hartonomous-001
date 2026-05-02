namespace Hartonomous.Engine.Godel;

/// <summary>
/// The classified intent of a sub-question. Drives arena selection in the
/// Orient phase: a definition prompt weighs lexical_disambiguation +
/// semantic_relevance heavily, a translation prompt weighs translation_quality,
/// a reference-quality prompt weighs source_authority + corroboration_strength.
/// Defaults to <see cref="Lookup"/> when no signal is detected.
/// </summary>
public enum PromptIntent
{
    /// <summary>"what is X", "define X", "describe X" — substrate's
    /// lemma/synset/gloss bridges are most relevant.</summary>
    Definition,

    /// <summary>"translate X to Y", "X in French" — translation_quality
    /// dominates; cross_lingual edges are the path.</summary>
    Translation,

    /// <summary>"how do I X", "explain how X works" — multi-step
    /// composition over typed edges.</summary>
    HowTo,

    /// <summary>"is X a Y", "are X always Y" — boolean target;
    /// significance threshold gates the answer.</summary>
    YesNo,

    /// <summary>"list X", "what are the X" — multiple targets expected;
    /// top-K should expand.</summary>
    Enumeration,

    /// <summary>Bare term lookup ("dog", "minute"). The substrate's full
    /// neighborhood is interesting; uniform arena weighting.</summary>
    Lookup,
}
