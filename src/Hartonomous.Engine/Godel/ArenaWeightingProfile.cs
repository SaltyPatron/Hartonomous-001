using System.Collections.Generic;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// Per-intent multipliers across the substrate's significance arenas. A
/// non-zero multiplier promotes that arena's edge weights in path scoring;
/// zero excludes it from the cross-arena fan-out for that sub-question.
/// The Orient phase reads the profile, applies the multipliers when
/// max-pooling traversal results, and the Decide phase consumes the
/// weighted scores to pick targets.
///
/// Weighting profiles are deterministic and arena-aware: adding a new
/// arena to <c>substrate.significance_context</c> means the profile maps
/// inherit a default weight (1.0) without any code change here. The map
/// is open-vocabulary (per AP-1).
/// </summary>
public sealed class ArenaWeightingProfile
{
    private const double DefaultWeight = 1.0;

    private readonly IReadOnlyDictionary<string, double> _weights;

    public ArenaWeightingProfile(IReadOnlyDictionary<string, double> weights)
    {
        _weights = weights;
    }

    public double WeightFor(string arenaCode) =>
        _weights.TryGetValue(arenaCode, out double w) ? w : DefaultWeight;

    public static ArenaWeightingProfile For(PromptIntent intent) => intent switch
    {
        PromptIntent.Definition => new ArenaWeightingProfile(new Dictionary<string, double>
        {
            ["lexical_disambiguation"]  = 1.5,
            ["semantic_relevance"]      = 1.5,
            ["corroboration_strength"]  = 1.3,
            ["source_authority"]        = 1.2,
            ["frequency_significance"]  = 1.0,
        }),
        PromptIntent.Translation => new ArenaWeightingProfile(new Dictionary<string, double>
        {
            ["translation_quality"]     = 2.0,
            ["lexical_disambiguation"]  = 1.2,
            ["source_authority"]        = 1.1,
        }),
        PromptIntent.HowTo => new ArenaWeightingProfile(new Dictionary<string, double>
        {
            ["semantic_relevance"]      = 1.5,
            ["corroboration_strength"]  = 1.3,
            ["source_authority"]        = 1.3,
            ["syntactic_role_fitness"]  = 1.2,
        }),
        PromptIntent.YesNo => new ArenaWeightingProfile(new Dictionary<string, double>
        {
            ["corroboration_strength"]  = 1.5,
            ["source_authority"]        = 1.4,
            ["semantic_relevance"]      = 1.2,
        }),
        PromptIntent.Enumeration => new ArenaWeightingProfile(new Dictionary<string, double>
        {
            ["semantic_relevance"]      = 1.4,
            ["frequency_significance"]  = 1.3,
            ["lexical_disambiguation"]  = 1.2,
        }),
        PromptIntent.Lookup => Uniform,
        _ => Uniform,
    };

    public static ArenaWeightingProfile Uniform { get; } =
        new ArenaWeightingProfile(new Dictionary<string, double>());
}
