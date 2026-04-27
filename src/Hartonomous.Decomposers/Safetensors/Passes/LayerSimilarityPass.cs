using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Pairwise cross-layer weight similarity via top-k singular-subspace alignment.
/// For each (role, layer) tensor (where role is one of Q / K / V / O / FFN-gate
/// / FFN-up / FFN-down) we compute the top-k left singular vectors once, then
/// for every (layer_i, layer_j) pair within the same role compute the Frobenius
/// norm of U_iᵀ · U_j. Because U_i and U_j are column-orthonormal, ‖U_iᵀU_j‖_F²
/// equals the sum of squared cosines of principal angles — a [0, k] similarity
/// score with k = perfect alignment and 0 = orthogonal subspaces.
///
/// Identical layers across two different models of the same architecture
/// therefore produce identical similarity scalars → identical content-hashed
/// <c>layer_similarity_pair</c> entities. Both layer indices are content for
/// this pairwise entity (per spec "layer and head indices ARE content for this
/// pairwise entity").
///
/// Depends on <c>model.svd</c> only for DAG ordering — we re-derive the left
/// singular vectors here from W·Wᵀ because <see cref="SvdPass"/> stores only the
/// singular values. Re-computation is bounded by the same gram-matrix cap
/// (<see cref="MaxGramSide"/>) and is deterministic on the same seed.
///
/// Per docs/specs/decomposers/analysis-passes.md § "LayerSimilarityPass".
/// </summary>
internal sealed partial class LayerSimilarityPass : IModelAnalysisPass
{
    public string PassId => "model.layer_similarity";
    public IReadOnlyList<string> Dependencies => ["model.svd"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopK = 8;
    private const int MaxGramSide = 4096;

    private static readonly TensorRole[] RolesToCompare =
    [
        TensorRole.AttentionQuery,
        TensorRole.AttentionKey,
        TensorRole.AttentionValue,
        TensorRole.AttentionOutput,
        TensorRole.FfnGate,
        TensorRole.FfnUp,
        TensorRole.FfnDown,
    ];

    private readonly ILogger _logger;

    public LayerSimilarityPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ulong baseSeed = context.DeriveSeed(PassId);

        foreach (TensorRole role in RolesToCompare)
        {
            ct.ThrowIfCancellationRequested();

            List<TensorHandle> layered = context.Tensors
                .Where(x => x.Classification.Role == role && x.Classification.LayerIndex is not null
                            && x.Info.Shape.Length == 2)
                .OrderBy(x => x.Classification.LayerIndex!.Value)
                .ToList();
            if (layered.Count < 2)
            {
                continue;
            }

            string roleCode = role.ToCode();
            Dictionary<int, double[]> leftSingular = new(layered.Count);
            foreach (TensorHandle t in layered)
            {
                ct.ThrowIfCancellationRequested();
                int m = (int)t.Info.Shape[0];
                int n = (int)t.Info.Shape[1];
                if (Math.Min(m, n) > MaxGramSide)
                {
                    Log.SkipTooLarge(_logger, t.Info.Name, m, n, MaxGramSide);
                    continue;
                }
                int k = Math.Min(TopK, Math.Min(m, n) - 1);
                if (k < 2)
                {
                    continue;
                }

                double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
                ulong tensorSeed = baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0);
                double[]? vecs = ComputeLeftSingularVectors(flat, m, n, k, tensorSeed);
                if (vecs is null)
                {
                    continue;
                }
                leftSingular[t.Classification.LayerIndex!.Value] = vecs;
                Log.LayerIndexed(_logger, roleCode, t.Classification.LayerIndex.Value, m, n, k);
            }

            List<int> layerIndices = leftSingular.Keys.OrderBy(x => x).ToList();
            for (int ii = 0; ii < layerIndices.Count; ii++)
            {
                for (int jj = ii + 1; jj < layerIndices.Count; jj++)
                {
                    ct.ThrowIfCancellationRequested();
                    int li = layerIndices[ii];
                    int lj = layerIndices[jj];
                    double[] ui = leftSingular[li];
                    double[] uj = leftSingular[lj];

                    // ui / uj packed column-major, shape [side × k]. Sides must match
                    // across layers of the same role to compare.
                    int sideI = ui.Length / TopK;
                    int sideJ = uj.Length / TopK;
                    if (sideI != sideJ)
                    {
                        Log.SideMismatch(_logger, role.ToCode(), li, lj, sideI, sideJ);
                        continue;
                    }
                    int side = sideI;
                    int k = TopK;

                    double[] product = new double[k * k];
                    Gemm.F64(
                        TransposeOp.Transpose, TransposeOp.None,
                        k, k, side,
                        1.0,
                        ui, k,
                        uj, k,
                        0.0,
                        product, k);

                    double sumSq = 0;
                    for (int p = 0; p < product.Length; p++)
                    {
                        sumSq += product[p] * product[p];
                    }
                    double similarity = sumSq / k;

                    // AP-9: hash covers SIMILARITY-FACT CONTENT only (the role family
                    // — Q/K/V/O — that was compared and the measured value). Architecture
                    // and (li, lj) layer indices are PLACEMENT metadata on the
                    // has_layer_similarity edge. Same similarity fact across (model,
                    // layer-pair) positions collapses to one entity → cross-model
                    // corroboration on layer-similarity patterns becomes possible.
                    CanonicalSignatureBuilder b = new(context.Compute.Common, "lsim");
                    b.WriteUtf8(role.ToCode());
                    b.WriteInt32LE(k);
                    b.WriteDouble(similarity);
                    byte[] hash = b.Finalize();

                    EntityHandle pair = session.Batch.AddEntity(hash, "layer_similarity_pair");
                    session.Batch.AddEntityModelSource(pair, context.Source.ModelSourceId);
                    session.Batch.AddEdge("has_layer_similarity", context.ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, context.Architecture.EntityId, "source", 0),
                        new EdgeMemberSpec(pair, null, "target", 1),
                    ]);

                    Log.PairComputed(_logger, roleCode, li, lj, similarity);
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Top-k left singular vectors of W (m×n) via Lanczos on the smaller of the
    /// two gram matrices. Returns vectors in column-major form, shape [side × k]
    /// where side = min(m, n). Null on failure / too-small.
    /// </summary>
    private static double[]? ComputeLeftSingularVectors(double[] w, int m, int n, int k, ulong seed)
    {
        int side = Math.Min(m, n);
        double[] gram = new double[(long)side * side];

        if (m >= n)
        {
            Gemm.F64(TransposeOp.Transpose, TransposeOp.None,
                n, n, m, 1.0, w, n, w, n, 0.0, gram, n);
        }
        else
        {
            Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                m, m, n, 1.0, w, n, w, n, 0.0, gram, m);
        }

        long nnz = (long)side * side;
        long[] rowPtr = new long[side + 1];
        long[] colIdx = new long[nnz];
        double[] values = new double[nnz];
        long p = 0;
        for (int i = 0; i < side; i++)
        {
            rowPtr[i] = p;
            for (int j = 0; j < side; j++)
            {
                colIdx[p] = j;
                values[p] = gram[(long)i * side + j];
                p++;
            }
        }
        rowPtr[side] = nnz;

        double[] evs = new double[k];
        double[] vecs = new double[(long)side * k];
        int maxIter = Math.Max(k + 8, 4 * k + 32);
        SparseEigsResult result = SparseSymEigs.F64(side, nnz, rowPtr, colIdx, values,
            k, maxIter, seed, evs, vecs);
        _ = result;

        // Repack column-major [side × k] → row-major [side rows × k cols]
        // so our downstream Gemm with ldA = k reads contiguous "columns".
        double[] packed = new double[(long)side * k];
        for (int col = 0; col < k; col++)
        {
            for (int row = 0; row < side; row++)
            {
                packed[(long)row * k + col] = vecs[(long)col * side + row];
            }
        }
        return packed;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[lsim] {Role} layer {Layer} indexed ({M}×{N}, top-{K})")]
        public static partial void LayerIndexed(ILogger logger, string role, int layer, int m, int n, int k);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[lsim] {Role} ({I}↔{J}) similarity={Sim:F4}")]
        public static partial void PairComputed(ILogger logger, string role, int i, int j, double sim);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[lsim] {Name} ({M}×{N}) exceeds gram cap {Cap}; skipped")]
        public static partial void SkipTooLarge(ILogger logger, string name, int m, int n, int cap);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[lsim] {Role} sides differ at ({I},{J}): {SI}≠{SJ}; skipped pair")]
        public static partial void SideMismatch(ILogger logger, string role, int i, int j, int si, int sj);
    }
}
