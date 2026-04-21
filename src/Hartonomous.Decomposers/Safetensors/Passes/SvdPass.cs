using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Top-k singular values per 2-D Track-2 weight matrix. The singular spectrum
/// encodes effective rank and transformation "intensity density" — a fast-decaying
/// spectrum means the matrix's useful transformation lives in a small subspace
/// (a compressibility signal the recomposer's distillation target consumes).
///
/// Implementation: σ_i(W) = √λ_i(Gram) where Gram is the smaller of W^T W
/// (n×n) or W W^T (m×m). The gram matrix is fully dense and deterministic;
/// we pack it as a dense-CSR matrix and run the facade's
/// <see cref="SparseSymEigs"/> Lanczos solver. Same weights → same gram →
/// same spectrum, bit-for-bit (Law #6).
///
/// Entity: <c>svd_spectrum</c>. Signature: parent tensor hash + truncation k
/// + top-k singular values packed f64 big-endian. Rank ordinals live on the
/// <c>spectrum_element</c> edges, never inside the entity hash.
///
/// Per docs/specs/decomposers/analysis-passes.md § "SvdPass".
/// </summary>
internal sealed partial class SvdPass : IModelAnalysisPass
{
    public string PassId => "model.svd";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopK = 16;
    // Cap the gram matrix side length we'll densely form. Attention/FFN matrices
    // above this would produce a gram with > MaxGramSide² ≈ 67M entries and the
    // Lanczos solver becomes impractically slow. Larger tensors are skipped with
    // a warning; the sparsity pass still profiles them.
    private const int MaxGramSide = 8192;

    private readonly ILogger _logger;

    public SvdPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ulong baseSeed = context.DeriveSeed(PassId);

        int tensorOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            if (!ShouldDecompose(t))
            {
                continue;
            }

            int m = (int)t.Info.Shape[0];
            int n = (int)t.Info.Shape[1];
            int side = Math.Min(m, n);
            if (side > MaxGramSide)
            {
                Log.SkipTooLarge(_logger, t.Info.Name, m, n, MaxGramSide);
                continue;
            }

            int k = Math.Min(TopK, side - 1);
            if (k < 2)
            {
                continue;
            }

            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, m, n, k);

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double[] gram = BuildGram(flat, m, n);
            int gramSide = side;

            SparseEigsResult result = DenseSymEigsTopK(gram, gramSide, k, baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0), out double[] eigenvalues);
            double[] singular = new double[k];
            for (int i = 0; i < k; i++)
            {
                double eig = eigenvalues[i];
                singular[i] = eig > 0 ? Math.Sqrt(eig) : 0;
            }

            // Deterministic ordering: descending, with stable tie-break on original index.
            int[] order = Enumerable.Range(0, k).ToArray();
            Array.Sort(order, (a, b) =>
            {
                int c = singular[b].CompareTo(singular[a]);
                return c != 0 ? c : a.CompareTo(b);
            });
            double[] sortedSingular = new double[k];
            for (int i = 0; i < k; i++)
            {
                sortedSingular[i] = singular[order[i]];
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, "svds");
            b.WriteHash(t.ContentHash);
            b.WriteInt32LE(k);
            for (int i = 0; i < k; i++)
            {
                b.WriteDouble(sortedSingular[i]);
            }
            byte[] hash = b.Finalize();

            EntityHandle spectrum = session.Batch.AddEntity(hash, "svd_spectrum");
            session.Batch.AddEntityModelSource(spectrum, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_spectrum", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(spectrum, null, "target", 1),
            ]);

            Log.TensorComplete(_logger, t.Info.Name, k, sortedSingular[0], sortedSingular[k - 1], result.Converged);
        }
        return Task.CompletedTask;
    }

    private static bool ShouldDecompose(TensorHandle t)
    {
        if (t.Classification.Role.IsTrack1())
        {
            return false;
        }
        return t.Info.Shape.Length == 2
               && t.Info.Shape[0] > 1
               && t.Info.Shape[1] > 1
               && t.Info.ElementCount > 0;
    }

    /// <summary>
    /// Builds the smaller of Wᵀ·W or W·Wᵀ densely. W is stored row-major (m×n).
    /// If m ≥ n we form Gram(n,n) = Wᵀ·W; otherwise Gram(m,m) = W·Wᵀ.
    /// </summary>
    private static double[] BuildGram(double[] w, int m, int n)
    {
        int side = Math.Min(m, n);
        double[] gram = new double[(long)side * side];

        if (m >= n)
        {
            // Gram[i,j] = Σ_p W[p,i] * W[p,j]
            Gemm.F64(
                TransposeOp.Transpose, TransposeOp.None,
                n, n, m,
                1.0,
                w, n,
                w, n,
                0.0,
                gram, n);
        }
        else
        {
            // Gram[i,j] = Σ_p W[i,p] * W[j,p]
            Gemm.F64(
                TransposeOp.None, TransposeOp.Transpose,
                m, m, n,
                1.0,
                w, n,
                w, n,
                0.0,
                gram, m);
        }

        return gram;
    }

    /// <summary>
    /// Top-k eigenvalues of a dense symmetric n×n matrix via sparse Lanczos.
    /// Pack the dense matrix as a dense-CSR (every entry stored) and call the
    /// existing sparse solver — inefficient memory-wise but deterministic and
    /// matches the facade contract.
    /// </summary>
    private static SparseEigsResult DenseSymEigsTopK(double[] dense, int side, int k, ulong seed, out double[] eigenvalues)
    {
        long n = side;
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
                values[p] = dense[(long)i * side + j];
                p++;
            }
        }
        rowPtr[side] = nnz;

        eigenvalues = new double[k];
        double[] vecs = new double[n * k];
        int maxIter = Math.Max(k + 8, 4 * k + 32);

        SparseEigsResult result = SparseSymEigs.F64(
            n, nnz, rowPtr, colIdx, values,
            k, maxIter, seed,
            eigenvalues, vecs);
        return result;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[svd {Idx}] {Name} ({M}×{N}) starting top-{K}")]
        public static partial void TensorStart(ILogger logger, int idx, string name, int m, int n, int k);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[svd] {Name} top-{K}: σ_1={S1:F4} σ_k={Sk:F4} converged={Converged}")]
        public static partial void TensorComplete(ILogger logger, string name, int k, double s1, double sk, bool converged);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[svd] {Name} ({M}×{N}) exceeds gram side cap {Cap}; skipped")]
        public static partial void SkipTooLarge(ILogger logger, string name, int m, int n, int cap);
    }
}
