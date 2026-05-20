namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Pre-build cost estimate for a Substrate Synthesis synthesis. Deterministic
/// projection from the recipe + substrate readiness — does NOT touch the
/// substrate state, does NOT allocate, runs in microseconds.
///
/// Returned by <see cref="SynthesisCostEstimator.EstimateAsync"/> before the
/// user commits to a synth run. The synth-time-seconds projection feeds
/// the website's pricing/quota API (per-tier monthly bear cap, per-bear
/// surcharge, enterprise custom quoting).
///
/// All fields are derived analytically from the recipe — no machine
/// learning, no historical-data lookup. Same recipe → same estimate.
/// </summary>
public sealed record SynthesisCostEstimate(
    // Recipe-derived shape (deterministic from architecture spec)
    long ParameterCount,
    long EmbeddingParameters,
    long PerLayerAttentionParameters,
    long PerLayerFfnParameters,
    long LayerNormParameters,

    // Output package size (bytes), per the selected output dtype
    long OutputSafetensorsBytes,
    int  DtypeBytesPerParameter,

    // Synth-time projection (seconds), broken down by phase
    double VocabSelectionSeconds,
    double AdjacencyBuildSeconds,
    double EmbeddingSynthSeconds,
    double PerLayerAttentionSeconds,
    double PerLayerFfnSeconds,
    double TokenizerExportSeconds,
    double TotalSeconds,

    // Memory peak (bytes) — adjacency matrix dominates for large vocab
    long PeakMemoryBytes,

    // Substrate-side cost signals (heuristic, derived from recipe arenas
    // and vocab budget — refined when substrate state is queryable)
    long EstimatedEdgesScanned,
    int  RecipeArenaCount,

    // Capability gates: which features the recipe requested + whether the
    // substrate is currently capable of fulfilling them
    bool RequiresMoE,
    bool RequiresLoRA,
    bool RequiresRoPE);
