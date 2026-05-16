namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// CSR adjacency of the substrate's per-arena edge_significance matrix
/// over a finite vocab of entity hashes. Built by
/// <see cref="SubstrateAdjacencyBuilder"/> and consumed by
/// <see cref="EmbeddingSynthesizer"/> / <see cref="AttentionSynthesizer"/>
/// / <see cref="FfnSynthesizer"/>.
/// </summary>
public sealed class SubstrateAdjacency
{
    public required int N { get; init; }
    public required long Nnz { get; init; }
    public required long[] RowPtr { get; init; }
    public required long[] ColIdx { get; init; }
    public required double[] Values { get; init; }
    public required double[] RowL1 { get; init; }
    public required long NonIsolatedNodes { get; init; }
}
