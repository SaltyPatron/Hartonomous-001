namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Per-arena LayerNorm γ (scale) and β (bias) values derived from the
/// substrate's entity_significance mean + stddev in that arena. Consumed
/// by <see cref="LayerNormSynthesizer.GammaFor"/> / <c>BetaFor</c>
/// to fill the per-layer LN tensors at substrate-derived values rather
/// than scaffold 1/0 init.
/// </summary>
public sealed class LayerNormStats
{
    public required string Arena { get; init; }
    public required double Gamma { get; init; }
    public required double Beta { get; init; }
    public required long RowCount { get; init; }
}
