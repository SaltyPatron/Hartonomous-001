using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Static-weight attention archetype classifier. For each (layer, head) in a
/// standard multi-head attention stack we extract the head's slice of the Q
/// and K projection matrices and build a small, deterministic feature vector
/// characterizing the head's transformation:
///
///   • F0 – F3: top-4 singular values of Q_h, scaled by √d_head.
///   • F4 – F7: top-4 singular values of K_h.
///   • F8:     alignment cosine between Q_h's top left-singular vector and
///             K_h's top left-singular vector (|Q_top · K_top|).
///   • F9:     Q_h column-mean L2 norm (positional-bias indicator — a head
///             whose Q column means are large tends to attend by position).
///
/// A real corpus-forward-pass classifier is out of scope here; this is the
/// weight-only archetype that the spec calls "a frozen deterministic probe
/// battery over static weights" — we keep the probes self-contained so no
/// external corpus bytes leak into the signature.
///
/// Entity: <c>attention_archetype</c> per (layer, head). Signature includes
/// architecture hash, layer index, head index, and the feature vector —
/// layer/head are CONTENT for this entity because they identify which head
/// of which architecture encodes this archetype, not "where in the file it
/// lived" (per the spec note under "Permitted signature inputs").
///
/// Depends on <c>model.svd</c> for DAG ordering only — we re-derive top
/// singular components here via a small Gram-matrix eigensolve.
///
/// Per docs/specs/decomposers/analysis-passes.md § "AttentionArchetypePass".
/// </summary>
internal sealed partial class AttentionArchetypePass : IModelAnalysisPass
{
    public string PassId => "model.attention_archetype";
    public IReadOnlyList<string> Dependencies => ["model.svd"];

    // Std multi-head self-attention families. Narrowed so we don't try to emit
    // archetypes for e.g. convolutional trunks. Cross-attention and MoE heads
    // are handled by their own passes.
    public IReadOnlyList<string> AppliesToArchitectures =>
    [
        "BertModel", "BertForMaskedLM", "BertForSequenceClassification",
        "RobertaModel",
        "DistilBertModel",
        "GPT2Model", "GPT2LMHeadModel",
        "LlamaForCausalLM", "LlamaModel",
        "Qwen2ForCausalLM", "Qwen2Model",
        "Qwen3ForCausalLM", "Qwen3Model",
        "MistralForCausalLM",
        "MiniLMModel", "SentenceTransformer",
        "DETRModel", "ConditionalDetrModel",
    ];

    private const int TopK = 4;
    private const int MaxHeadDim = 512;

    private readonly ILogger _logger;

    public AttentionArchetypePass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int numHeads = context.Architecture.Architecture.NumAttentionHeads;
        int hiddenSize = context.Architecture.Architecture.HiddenSize;
        if (numHeads <= 0 || hiddenSize <= 0 || hiddenSize % numHeads != 0)
        {
            Log.InvalidArch(_logger, numHeads, hiddenSize);
            return Task.CompletedTask;
        }
        int dHead = hiddenSize / numHeads;
        if (dHead > MaxHeadDim)
        {
            Log.HeadTooLarge(_logger, dHead, MaxHeadDim);
            return Task.CompletedTask;
        }

        ulong baseSeed = context.DeriveSeed(PassId);

        // Build layer → { q, k } map.
        Dictionary<int, (TensorHandle? Q, TensorHandle? K)> byLayer = new();
        foreach (TensorHandle t in context.Tensors)
        {
            if (t.Classification.LayerIndex is not int layer)
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                continue;
            }
            var existing = byLayer.GetValueOrDefault(layer);
            switch (t.Classification.Role)
            {
                case TensorRole.AttentionQuery:
                    byLayer[layer] = (t, existing.K);
                    break;
                case TensorRole.AttentionKey:
                    byLayer[layer] = (existing.Q, t);
                    break;
            }
        }

        foreach (int layer in byLayer.Keys.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            (TensorHandle? q, TensorHandle? k) = byLayer[layer];
            if (q is null || k is null)
            {
                continue;
            }
            if (q.Info.Shape[1] != hiddenSize || k.Info.Shape[1] != hiddenSize)
            {
                Log.LayerShapeMismatch(_logger, layer, (int)q.Info.Shape[0], (int)q.Info.Shape[1], hiddenSize);
                continue;
            }

            double[] qFlat = SafetensorsReader.ReadTensorAsDouble(q.Info);
            double[] kFlat = SafetensorsReader.ReadTensorAsDouble(k.Info);

            // Q/K stored row-major as [hidden_out, hidden_in]. Head h owns rows
            // [h*d_head : (h+1)*d_head). This is the common Llama/BERT convention;
            // models with head-interleaved storage would need a different slicer.
            // GQA (fewer K heads than Q heads) is handled by mapping q heads to
            // their owning k head via integer division.
            int qRows = (int)q.Info.Shape[0];
            int kRows = (int)k.Info.Shape[0];
            int qNumHeads = qRows / dHead;
            int kNumHeads = kRows / dHead;
            if (qNumHeads == 0 || kNumHeads == 0 || qRows % dHead != 0 || kRows % dHead != 0)
            {
                Log.LayerHeadDivide(_logger, layer, qRows, kRows, dHead);
                continue;
            }

            for (int head = 0; head < qNumHeads; head++)
            {
                ct.ThrowIfCancellationRequested();
                int kHead = head % kNumHeads;
                double[] qHead = ExtractHeadRows(qFlat, hiddenSize, dHead, head);
                double[] kHeadMat = ExtractHeadRows(kFlat, hiddenSize, dHead, kHead);

                // Top-k singular vals of Q_h and K_h via QᵀQ / KᵀK eigensolve.
                ulong seed = baseSeed ^ (ulong)((long)layer << 32 | (uint)head);
                double[] qSing = TopSingularValues(qHead, dHead, hiddenSize, TopK, seed, out double[] qTopLeft);
                double[] kSing = TopSingularValues(kHeadMat, dHead, hiddenSize, TopK, seed ^ 0x9E3779B97F4A7C15UL, out double[] kTopLeft);

                double alignment = Math.Abs(Dot(qTopLeft, kTopLeft, dHead));
                double posBias = ColumnMeanL2Norm(qHead, dHead, hiddenSize);

                double sqrtD = Math.Sqrt(dHead);
                double[] feat = new double[10];
                for (int i = 0; i < TopK; i++)
                {
                    feat[i] = qSing[i] / sqrtD;
                    feat[TopK + i] = kSing[i] / sqrtD;
                }
                feat[8] = alignment;
                feat[9] = posBias;

                CanonicalSignatureBuilder b = new(context.Compute.Common, "atnh");
                b.WriteHash(context.Architecture.ContentHash);
                b.WriteInt32LE(layer);
                b.WriteInt32LE(head);
                for (int i = 0; i < feat.Length; i++)
                {
                    b.WriteDouble(feat[i]);
                }
                byte[] hash = b.Finalize();

                EntityHandle arch = session.Batch.AddEntity(hash, "attention_archetype");
                session.Batch.AddEntityModelSource(arch, context.Source.ModelSourceId);

                // Role disambiguation (Q vs K projection) lives on the source
                // tensor's tensor_role junction, not on edge_role here. Both
                // edges use 'source' generically; the substrate-level Q/K
                // distinction is recovered by joining edge.source → tensor →
                // tensor_tensor_role.
                session.Batch.AddEdge("encodes_archetype", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(null, q.EntityId, "source", 0),
                    new EdgeMemberSpec(arch, null, "target", 1),
                ]);
                session.Batch.AddEdge("encodes_archetype", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(null, k.EntityId, "source", 0),
                    new EdgeMemberSpec(arch, null, "target", 1),
                ]);

                Log.HeadArchetype(_logger, layer, head, qSing[0], kSing[0], alignment, posBias);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Rows [head·dHead : (head+1)·dHead) of a row-major [out, in] matrix.</summary>
    private static double[] ExtractHeadRows(double[] w, int inDim, int dHead, int head)
    {
        double[] slice = new double[(long)dHead * inDim];
        int rowStart = head * dHead;
        for (int r = 0; r < dHead; r++)
        {
            Array.Copy(w, (long)(rowStart + r) * inDim, slice, (long)r * inDim, inDim);
        }
        return slice;
    }

    /// <summary>
    /// Top-k singular values of W (m×n) via Gram eigensolve, and the top left
    /// singular vector (length m) corresponding to the largest singular value.
    /// W is stored row-major.
    /// </summary>
    private static double[] TopSingularValues(double[] w, int m, int n, int k, ulong seed, out double[] topLeftVec)
    {
        int side = Math.Min(m, n);
        int kEff = Math.Min(k, side - 1);
        if (kEff < 1)
        {
            topLeftVec = new double[m];
            return new double[k];
        }

        double[] gram = new double[(long)side * side];
        bool useRight = m >= n;
        if (useRight)
        {
            Hartonomous.Core.Compute.Ingestion.Gemm.F64(
                Hartonomous.Core.Compute.Ingestion.TransposeOp.Transpose,
                Hartonomous.Core.Compute.Ingestion.TransposeOp.None,
                n, n, m, 1.0, w, n, w, n, 0.0, gram, n);
        }
        else
        {
            Hartonomous.Core.Compute.Ingestion.Gemm.F64(
                Hartonomous.Core.Compute.Ingestion.TransposeOp.None,
                Hartonomous.Core.Compute.Ingestion.TransposeOp.Transpose,
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

        double[] evs = new double[kEff];
        double[] vecs = new double[(long)side * kEff];
        int maxIter = Math.Max(kEff + 8, 4 * kEff + 32);
        Hartonomous.Core.Compute.Ingestion.SparseSymEigs.F64(
            side, nnz, rowPtr, colIdx, values,
            kEff, maxIter, seed, evs, vecs);

        double[] singular = new double[k];
        // Descending order via sort on absolute eigenvalue (gram is PSD so evs ≥ 0).
        int[] ord = Enumerable.Range(0, kEff).ToArray();
        Array.Sort(ord, (a, b) => evs[b].CompareTo(evs[a]));
        for (int i = 0; i < kEff; i++)
        {
            double eig = evs[ord[i]];
            singular[i] = eig > 0 ? Math.Sqrt(eig) : 0;
        }

        // Top left singular vector: if we used W·Wᵀ gram, the top eigenvector is
        // already the left singular vector. If we used Wᵀ·W, we derive u₁ =
        // (1/σ₁) · W · v₁.
        topLeftVec = new double[m];
        int bestIdx = ord[0];
        if (!useRight)
        {
            for (int i = 0; i < m; i++)
            {
                topLeftVec[i] = vecs[(long)bestIdx * side + i];
            }
        }
        else
        {
            double sig = singular[0];
            if (sig > 1e-12)
            {
                double[] v1 = new double[n];
                for (int i = 0; i < n; i++)
                {
                    v1[i] = vecs[(long)bestIdx * side + i];
                }
                for (int i = 0; i < m; i++)
                {
                    double acc = 0;
                    long ri = (long)i * n;
                    for (int j = 0; j < n; j++)
                    {
                        acc += w[ri + j] * v1[j];
                    }
                    topLeftVec[i] = acc / sig;
                }
            }
        }
        NormalizeInPlace(topLeftVec);
        return singular;
    }

    private static double Dot(double[] a, double[] b, int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            s += a[i] * b[i];
        }
        return s;
    }

    private static void NormalizeInPlace(double[] v)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++)
        {
            s += v[i] * v[i];
        }
        s = Math.Sqrt(s);
        if (s < 1e-12)
        {
            return;
        }
        double inv = 1.0 / s;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }
    }

    private static double ColumnMeanL2Norm(double[] w, int m, int n)
    {
        double[] colMean = new double[n];
        for (int r = 0; r < m; r++)
        {
            long rowBase = (long)r * n;
            for (int c = 0; c < n; c++)
            {
                colMean[c] += w[rowBase + c];
            }
        }
        double invM = 1.0 / m;
        double s = 0;
        for (int c = 0; c < n; c++)
        {
            double v = colMean[c] * invM;
            s += v * v;
        }
        return Math.Sqrt(s);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[atnh] bad architecture: heads={Heads}, hidden={Hidden}; skipped")]
        public static partial void InvalidArch(ILogger logger, int heads, int hidden);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[atnh] head dim {DHead} exceeds cap {Cap}; skipped")]
        public static partial void HeadTooLarge(ILogger logger, int dHead, int cap);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[atnh layer={Layer}] qShape=({QM}×{QN}) but hidden={Hidden}; skipped")]
        public static partial void LayerShapeMismatch(ILogger logger, int layer, int qm, int qn, int hidden);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[atnh layer={Layer}] head dim divides rows unevenly qRows={QRows} kRows={KRows} dHead={DHead}; skipped")]
        public static partial void LayerHeadDivide(ILogger logger, int layer, int qRows, int kRows, int dHead);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[atnh {Layer}/{Head}] σQ={QS:F3} σK={KS:F3} align={Align:F3} pos={Pos:F4}")]
        public static partial void HeadArchetype(ILogger logger, int layer, int head, double qs, double ks, double align, double pos);
    }
}
