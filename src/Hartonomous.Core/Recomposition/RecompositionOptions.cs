namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Options the recomposer applies at materialization time. Substrate storage
/// is always lossless (full f64 row content per per-role unit); these knobs
/// are how the export filters that lossless storage into the substrate's
/// "denser than source" output (Substrate Law #11 — gradient jitter does
/// not survive recomposition).
/// </summary>
public sealed record RecompositionOptions
{
    public int MaxDepth { get; init; } = int.MaxValue;

    /// <summary>
    /// Glicko-2 mu floor. Per-role units below this rating in the chosen
    /// arena are treated as if the substrate had nothing for that placement
    /// — their position in the output stays zero. 0 = no rating filter.
    /// </summary>
    public double SignificanceThreshold { get; init; }

    /// <summary>Arena code the SignificanceThreshold gates against.</summary>
    public string? ArenaFilter { get; init; }

    public bool IncludeProvenance { get; init; }

    /// <summary>
    /// Per-element magnitude floor at recompose time. Each row value whose
    /// |x| is below this is written to the export as exactly 0. The
    /// substrate still STORES the original lossless value; this filter
    /// applies only to the export bytes. Result: the exported file is
    /// genuinely denser than the source — the substrate's accumulated
    /// "what is signal vs jitter" decision is enforced at materialization,
    /// per Law #11. 0 = no filter (byte-exact round-trip).
    /// Recommended starting value for BF16/F32 transformer weights: 1e-3.
    /// </summary>
    public double NoiseFloor { get; init; }
}
