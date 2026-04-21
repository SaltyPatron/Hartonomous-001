using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Top-k eigenvalues of the symmetrized form (W + Wᵀ)/2 for square weight
/// matrices. When the transformation is naturally interpretable as a linear
/// operator on its own column space, the symmetric part captures the operator's
/// self-adjoint contribution — eigenvalues whose sign indicates whether the
/// operator amplifies or contracts along each eigen direction.
///
/// Runs only on square 2-D tensors. Row-major; packed as dense-CSR and routed
/// through the facade's <see cref="SparseSymEigs"/> Lanczos solver.
///
/// Entity: <c>eigenvalue_spectrum</c>. Signature: parent tensor hash + k +
/// eigenvalues sorted descending, packed as f64 BE. Deterministic across runs
/// on identical input (Law #6).
///
/// Per docs/specs/decomposers/analysis-passes.md § "EigenvaluePass".
/// </summary>
internal sealed partial class EigenvaluePass : IModelAnalysisPass
{
    public string PassId => "model.eigenvalues";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopK = 16;
    private const int MaxSide = 8192;

    private readonly ILogger _logger;

    public EigenvaluePass(ILogger logger)
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

            if (!ShouldApply(t))
            {
                continue;
            }

            int side = (int)t.Info.Shape[0];
            if (side > MaxSide)
            {
                Log.SkipTooLarge(_logger, t.Info.Name, side, MaxSide);
                continue;
            }

            int k = Math.Min(TopK, side - 1);
            if (k < 2)
            {
                continue;
            }

            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, side, k);

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double[] sym = Symmetrize(flat, side);

            SparseEigsResult result = DenseSymEigsTopK(sym, side, k,
                baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0),
                out double[] eigenvalues);

            int[] order = Enumerable.Range(0, k).ToArray();
            Array.Sort(order, (a, b) =>
            {
                int c = eigenvalues[b].CompareTo(eigenvalues[a]);
                return c != 0 ? c : a.CompareTo(b);
            });
            double[] sorted = new double[k];
            for (int i = 0; i < k; i++)
            {
                sorted[i] = eigenvalues[order[i]];
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, "eigs");
            b.WriteHash(t.ContentHash);
            b.WriteInt32LE(k);
            for (int i = 0; i < k; i++)
            {
                b.WriteDouble(sorted[i]);
            }
            byte[] hash = b.Finalize();

            EntityHandle spectrum = session.Batch.AddEntity(hash, "eigenvalue_spectrum");
            session.Batch.AddEntityModelSource(spectrum, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_eigenvalue_spectrum", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(spectrum, null, "target", 1),
            ]);

            Log.TensorComplete(_logger, t.Info.Name, k, sorted[0], sorted[k - 1], result.Converged);
        }
        return Task.CompletedTask;
    }

    private static bool ShouldApply(TensorHandle t)
    {
        if (t.Classification.Role.IsTrack1())
        {
            return false;
        }
        return t.Info.Shape.Length == 2
               && t.Info.Shape[0] == t.Info.Shape[1]
               && t.Info.Shape[0] > 1
               && t.Info.ElementCount > 0;
    }

    private static double[] Symmetrize(double[] w, int side)
    {
        double[] sym = new double[(long)side * side];
        for (int i = 0; i < side; i++)
        {
            long ri = (long)i * side;
            for (int j = 0; j < side; j++)
            {
                double a = w[ri + j];
                double b = w[(long)j * side + i];
                sym[ri + j] = 0.5 * (a + b);
            }
        }
        return sym;
    }

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
        return SparseSymEigs.F64(
            n, nnz, rowPtr, colIdx, values,
            k, maxIter, seed,
            eigenvalues, vecs);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[eig {Idx}] {Name} ({Side}²) starting top-{K}")]
        public static partial void TensorStart(ILogger logger, int idx, string name, int side, int k);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[eig] {Name} top-{K}: λ_1={L1:F4} λ_k={Lk:F4} converged={Converged}")]
        public static partial void TensorComplete(ILogger logger, string name, int k, double l1, double lk, bool converged);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[eig] {Name} square side {Side} exceeds cap {Cap}; skipped")]
        public static partial void SkipTooLarge(ILogger logger, string name, int side, int cap);
    }
}
