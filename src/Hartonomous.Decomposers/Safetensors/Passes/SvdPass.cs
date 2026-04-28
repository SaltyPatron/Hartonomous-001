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
    // Boundary between dense Gram and matrix-free Lanczos. Below this side the
    // dense path is faster (one MKL Lanczos call); above it dense becomes
    // memory-prohibitive (Gram is side² doubles). The streaming path scales
    // with disk I/O instead of memory and removes the prior silent-skip on
    // large tensors. Existing svd_spectrum entity hashes are preserved for
    // tensors that fit under DenseGramSide because the dense path is unchanged.
    private const int DenseGramSide = 8192;

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

            int k = Math.Min(TopK, side - 1);
            if (k < 2)
            {
                continue;
            }

            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, m, n, k);
            ulong tensorSeed = baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0);

            double[] eigenvalues;
            double[] eigvecs;             // column-major [side, k] — Gram-side eigenvectors
            double[]? denseFlat = null;   // populated only on dense path; reused for U/V reconstruction
            SparseEigsResult result;
            if (side <= DenseGramSide)
            {
                // Small tensor: dense Gram + MKL Lanczos. Identical to prior path
                // so existing svd_spectrum entity hashes are preserved bit-for-bit.
                denseFlat = SafetensorsReader.ReadTensorAsDouble(t.Info);
                double[] gram = BuildGram(denseFlat, m, n);
                result = DenseSymEigsTopK(gram, side, k, tensorSeed, out eigenvalues, out eigvecs);
            }
            else
            {
                // Large tensor: matrix-free streaming Lanczos. The Gram is never
                // materialized; matvec streams W through SafetensorsReader twice
                // per iteration. Removes the prior silent skip on tensors above
                // DenseGramSide (typically the FFN matrices that dominate large
                // models' parameter counts).
                Log.TensorStreaming(_logger, t.Info.Name, m, n, side);
                int maxIter = Math.Max(k + 8, 4 * k + 32);
                eigenvalues = new double[k];
                eigvecs = new double[(long)side * k];
                StreamingLanczos.MatvecF64 matvec = BuildStreamingGramMatvec(t.Info, m, n);
                result = StreamingLanczos.F64(side, matvec, k, maxIter, tensorSeed, eigenvalues, eigvecs);
            }
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
                new EdgeMemberSpec(t.Entity, "source", 0),
                new EdgeMemberSpec(spectrum, "target", 1),
            ]);

            // Per-rank components (migration 0043): for each rank, emit one
            // svd_rank_component entity carrying (rank_index, σ, U_col, V_row)
            // as canonical content, plus a singular_vector_pair physicality
            // (linestring4d packing U⊕V in 4-tuples, padded). The recomposer
            // walks has_rank_component edges to find these and scatters their
            // content back into the target tensor's rows/cols at synthesis
            // time. Emits only when k ≥ 2 components survived; ordered by
            // descending σ for downstream traversal determinism.
            EmitRankComponents(
                context, session, t,
                m, n, side, k,
                sortedSingular, order,
                eigvecs, denseFlat);

            Log.TensorComplete(_logger, t.Info.Name, k, sortedSingular[0], sortedSingular[k - 1], result.Converged);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Per-rank emission. For each surviving rank, recover the (U_col, V_row)
    /// pair from the Gram-side eigenvectors plus one matrix-vector product
    /// against W (or its dense in-memory flat form on the small path).
    /// </summary>
    private static void EmitRankComponents(
        ModelPassContext context,
        IPassSession session,
        TensorHandle t,
        int m, int n, int side, int k,
        double[] sortedSingular,
        int[] order,
        double[] eigvecsGramSide,    // column-major [side, k] in original Lanczos order
        double[]? denseFlat)
    {
        bool useRightGram = m >= n;

        // Reconstruct full U[m,k] and V[n,k] in column-major form so each rank
        // has a contiguous slice. The Gram-side eigenvectors are V columns
        // when m ≥ n (side = n), or U columns when m < n (side = m). The
        // missing side comes from one matvec per rank against W.
        double[] uMat = new double[(long)m * k];
        double[] vMat = new double[(long)n * k];

        if (useRightGram)
        {
            // V[:, i] = eigvecs[:, original_idx] where original_idx = order[rank].
            // U[:, i] = (1/σ_i) · W · V[:, i].
            for (int rank = 0; rank < k; rank++)
            {
                int origIdx = order[rank];
                long vDst = (long)rank * n;
                long vSrc = (long)origIdx * side;   // side == n when useRightGram
                for (int j = 0; j < n; j++) { vMat[vDst + j] = eigvecsGramSide[vSrc + j]; }
            }
            ReconstructUFromV(t.Info, m, n, vMat, sortedSingular, k, uMat, denseFlat);
        }
        else
        {
            // eigvecs columns are U vectors (length m). V[:, i] = (1/σ_i) · Wᵀ · U[:, i].
            for (int rank = 0; rank < k; rank++)
            {
                int origIdx = order[rank];
                long uDst = (long)rank * m;
                long uSrc = (long)origIdx * side;   // side == m when !useRightGram
                for (int j = 0; j < m; j++) { uMat[uDst + j] = eigvecsGramSide[uSrc + j]; }
            }
            ReconstructVFromU(t.Info, m, n, uMat, sortedSingular, k, vMat, denseFlat);
        }

        // Emit one svd_rank_component entity per rank with σ ≥ a tiny floor.
        for (int rank = 0; rank < k; rank++)
        {
            double sigma = sortedSingular[rank];
            if (sigma <= 0) { continue; }   // sparsity: zero singular values are noise floor

            // Sign-normalize so two models that produce the same direction up
            // to a synchronized U,V flip dedupe to one entity. Pin: flip both
            // when U_col's first non-tiny entry is negative.
            long uOff = (long)rank * m;
            long vOff = (long)rank * n;
            NormalizeSignConvention(uMat, uOff, m, vMat, vOff, n);

            // Canonical hash: kind tag + parent tensor hash + rank_index +
            // σ + U_col_bytes + V_row_bytes. rank_index IS content for this
            // entity (rank-3 of one tensor is a different entity from rank-3
            // of another; the parent hash differentiates the tensor and the
            // rank_index differentiates the position).
            CanonicalSignatureBuilder sb = new(context.Compute.Common, "svrk");
            sb.WriteHash(t.ContentHash);
            sb.WriteInt32LE(rank);
            sb.WriteDouble(sigma);
            for (int j = 0; j < m; j++) { sb.WriteDouble(uMat[uOff + j]); }
            for (int j = 0; j < n; j++) { sb.WriteDouble(vMat[vOff + j]); }
            byte[] rankHash = sb.Finalize();

            EntityHandle comp = session.Batch.AddEntity(rankHash, "svd_rank_component");
            session.Batch.AddEntityModelSource(comp, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_rank_component", context.ProvenanceCode,
            [
                new EdgeMemberSpec(t.Entity, "source", 0),
                new EdgeMemberSpec(comp, "target", 1),
            ]);

            // Pack σ⊕U⊕V into linestring4d 4-tuples. Layout: index 0 = σ,
            // indices 1..m+1 = U_col, indices m+1..m+n+1 = V_row, rest pad.
            // σ is stored explicitly so the recomposer can reconstruct
            // W ≈ Σ σ_i · u_i · v_iᵀ with the correct magnitudes; the
            // entity hash signature also carries σ separately for identity.
            int totalLen = 1 + m + n;
            int vertexCount = (totalLen + 3) / 4;
            (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                int b = v * 4;
                verts[v] = (
                    SigmaUvAt(sigma, uMat, uOff, m, vMat, vOff, n, b),
                    SigmaUvAt(sigma, uMat, uOff, m, vMat, vOff, n, b + 1),
                    SigmaUvAt(sigma, uMat, uOff, m, vMat, vOff, n, b + 2),
                    SigmaUvAt(sigma, uMat, uOff, m, vMat, vOff, n, b + 3));
            }
            session.Batch.AddPhysicalityLineString4d(comp, "contour", verts.AsSpan());
        }
    }

    /// <summary>σ at index 0, U at indices 1..m+1, V at indices m+1..m+n+1, pad otherwise.</summary>
    private static double SigmaUvAt(double sigma, double[] uMat, long uOff, int mm, double[] vMat, long vOff, int nn, int idx)
    {
        if (idx == 0) { return sigma; }
        int uIdx = idx - 1;
        if (uIdx < mm) { return uMat[uOff + uIdx]; }
        int vIdx = uIdx - mm;
        if (vIdx < nn) { return vMat[vOff + vIdx]; }
        return 0.0;
    }

    private static void NormalizeSignConvention(double[] uMat, long uOff, int mm, double[] vMat, long vOff, int nn)
    {
        const double tiny = 1e-12;
        for (int j = 0; j < mm; j++)
        {
            double a = uMat[uOff + j];
            if (a > tiny) { return; }
            if (a < -tiny)
            {
                for (int i = 0; i < mm; i++) { uMat[uOff + i] = -uMat[uOff + i]; }
                for (int i = 0; i < nn; i++) { vMat[vOff + i] = -vMat[vOff + i]; }
                return;
            }
        }
    }

    /// <summary>
    /// U[:, i] = (1/σ_i) · W · V[:, i] for all ranks. Single streaming pass
    /// over W (or single dense pass when denseFlat is in memory) accumulating
    /// into all k output columns at once.
    /// </summary>
    private static void ReconstructUFromV(
        SafetensorsTensorInfo tensor, int m, int n,
        double[] vMat, double[] sortedSingular, int k,
        double[] uMat, double[]? denseFlat)
    {
        Array.Clear(uMat);
        if (denseFlat is not null)
        {
            for (int rank = 0; rank < k; rank++)
            {
                long uOff = (long)rank * m;
                long vOff = (long)rank * n;
                for (int r = 0; r < m; r++)
                {
                    double s = 0;
                    long wRow = (long)r * n;
                    for (int c = 0; c < n; c++) { s += denseFlat[wRow + c] * vMat[vOff + c]; }
                    uMat[uOff + r] = s;
                }
            }
        }
        else
        {
            int row = 0, col = 0;
            SafetensorsReader.StreamDecode(tensor, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (row < m)
                    {
                        double w = chunk[i];
                        for (int rank = 0; rank < k; rank++)
                        {
                            uMat[(long)rank * m + row] += w * vMat[(long)rank * n + col];
                        }
                    }
                    col++;
                    if (col >= n) { col = 0; row++; }
                }
            });
        }
        for (int rank = 0; rank < k; rank++)
        {
            double sig = sortedSingular[rank];
            double inv = sig > 1e-12 ? 1.0 / sig : 0.0;
            long uOff = (long)rank * m;
            for (int j = 0; j < m; j++) { uMat[uOff + j] *= inv; }
        }
    }

    /// <summary>V[:, i] = (1/σ_i) · Wᵀ · U[:, i] for all ranks.</summary>
    private static void ReconstructVFromU(
        SafetensorsTensorInfo tensor, int m, int n,
        double[] uMat, double[] sortedSingular, int k,
        double[] vMat, double[]? denseFlat)
    {
        Array.Clear(vMat);
        if (denseFlat is not null)
        {
            for (int rank = 0; rank < k; rank++)
            {
                long uOff = (long)rank * m;
                long vOff = (long)rank * n;
                for (int c = 0; c < n; c++)
                {
                    double s = 0;
                    for (int r = 0; r < m; r++) { s += denseFlat[(long)r * n + c] * uMat[uOff + r]; }
                    vMat[vOff + c] = s;
                }
            }
        }
        else
        {
            int row = 0, col = 0;
            SafetensorsReader.StreamDecode(tensor, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (row < m)
                    {
                        double w = chunk[i];
                        for (int rank = 0; rank < k; rank++)
                        {
                            vMat[(long)rank * n + col] += w * uMat[(long)rank * m + row];
                        }
                    }
                    col++;
                    if (col >= n) { col = 0; row++; }
                }
            });
        }
        for (int rank = 0; rank < k; rank++)
        {
            double sig = sortedSingular[rank];
            double inv = sig > 1e-12 ? 1.0 / sig : 0.0;
            long vOff = (long)rank * n;
            for (int j = 0; j < n; j++) { vMat[vOff + j] *= inv; }
        }
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
    private static SparseEigsResult DenseSymEigsTopK(
        double[] dense, int side, int k, ulong seed,
        out double[] eigenvalues, out double[] eigvecs)
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
        eigvecs = new double[n * k];
        int maxIter = Math.Max(k + 8, 4 * k + 32);

        SparseEigsResult result = SparseSymEigs.F64(
            n, nnz, rowPtr, colIdx, values,
            k, maxIter, seed,
            eigenvalues, eigvecs);
        return result;
    }

    /// <summary>
    /// Builds a matvec closure y = G·x where G is the smaller of WᵀW (n×n,
    /// when m ≥ n) or WWᵀ (m×m, when m &lt; n). Streams W from disk twice
    /// per matvec via SafetensorsReader.StreamDecode — once to form the
    /// intermediate vector, once to project back. Memory scales O(side)
    /// instead of O(side²), removing the dense-Gram cap.
    /// </summary>
    private static StreamingLanczos.MatvecF64 BuildStreamingGramMatvec(
        SafetensorsTensorInfo tensor, int m, int n)
    {
        bool useRightGram = m >= n;
        int side = Math.Min(m, n);
        int otherSide = Math.Max(m, n);
        double[] xBuf = new double[side];
        double[] tmpBuf = new double[otherSide];
        double[] yBuf = new double[side];

        return (xSpan, ySpan) =>
        {
            xSpan.CopyTo(xBuf);
            if (useRightGram)
            {
                ComputeWxStreaming(tensor, m, n, xBuf, tmpBuf);
                ComputeWtxStreaming(tensor, m, n, tmpBuf, yBuf);
            }
            else
            {
                ComputeWtxStreaming(tensor, m, n, xBuf, tmpBuf);
                ComputeWxStreaming(tensor, m, n, tmpBuf, yBuf);
            }
            yBuf.AsSpan(0, ySpan.Length).CopyTo(ySpan);
        };
    }

    /// <summary>result[r] = Σ_c W[r,c] · x[c] for a row-major tensor streamed sequentially.</summary>
    private static void ComputeWxStreaming(
        SafetensorsTensorInfo tensor, int m, int n, double[] x, double[] result)
    {
        Array.Clear(result, 0, m);
        int row = 0;
        int col = 0;
        SafetensorsReader.StreamDecode(tensor, chunk =>
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                if (row < m)
                {
                    result[row] += chunk[i] * x[col];
                }
                col++;
                if (col >= n)
                {
                    col = 0;
                    row++;
                }
            }
        });
    }

    /// <summary>result[c] = Σ_r W[r,c] · x[r] for a row-major tensor streamed sequentially.</summary>
    private static void ComputeWtxStreaming(
        SafetensorsTensorInfo tensor, int m, int n, double[] x, double[] result)
    {
        Array.Clear(result, 0, n);
        int row = 0;
        int col = 0;
        SafetensorsReader.StreamDecode(tensor, chunk =>
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                if (row < m)
                {
                    result[col] += chunk[i] * x[row];
                }
                col++;
                if (col >= n)
                {
                    col = 0;
                    row++;
                }
            }
        });
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[svd {Idx}] {Name} ({M}×{N}) starting top-{K}")]
        public static partial void TensorStart(ILogger logger, int idx, string name, int m, int n, int k);

        [LoggerMessage(Level = LogLevel.Information, Message = "[svd] {Name} ({M}×{N}, side={Side}) using streaming matvec — gram never materialized, no cap")]
        public static partial void TensorStreaming(ILogger logger, string name, int m, int n, int side);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[svd] {Name} top-{K}: σ_1={S1:F4} σ_k={Sk:F4} converged={Converged}")]
        public static partial void TensorComplete(ILogger logger, string name, int k, double s1, double sk, bool converged);

    }
}
