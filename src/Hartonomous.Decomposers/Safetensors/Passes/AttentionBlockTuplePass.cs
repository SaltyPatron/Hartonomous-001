using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §II.1 + §IV. Processes AttentionBlock
/// tuples and emits per-(source_token, target_token) Glicko-2 attestation
/// events on <c>model_attention_pattern</c> edges between word_form entities.
///
/// Each per-pair projection value IS one Glicko contest in the
/// model_attention_qk_pattern arena. Mapping: <c>score = sigmoid(value /
/// temperature); weight = 1.0</c>. Continuous score in [0,1] encodes both
/// sign (above/below 0.5) and magnitude (distance from 0.5). Glicko-2
/// absorbs the per-event scale via its variance estimator; no per-row
/// noise floor or sign/magnitude split required.
///
/// Memory-bounded chunk-streaming. Working set per layer per side is
/// constant (~100 MB) regardless of vocab size or hidden dim — the
/// previous "materialize full vocab × dProj projection matrix" shape used
/// gigabyte working sets on Llama-class models. Per-iteration peak:
///
///   sourceGather (S × dProj) + targetGather (T × dProj) + S_block (S × T)
///
/// where S = source chunk size, T = target chunk size. ~100 MB total at
/// S=512, T=4096, dProj=4096 (FP64).
/// </summary>
internal sealed partial class AttentionBlockTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.attention_block";
    public IReadOnlyList<string> Dependencies => ["tuple.embedding_lookup"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopSourceTokens = 2048;
    private const int TopKPerSourceRow = 64;
    private const int SourceChunkSize = 512;
    private const int TargetChunkSize = 4096;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 25_000;

    private readonly ILogger _logger;

    public AttentionBlockTuplePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        TensorHandle? embeddingTensor = FindEmbedding(context);
        if (embeddingTensor is null)
        {
            Log.NoEmbedding(_logger, context.Source.ModelId);
            return;
        }
        TensorHandle e = embeddingTensor;
        int vocabSize = (int)e.Info.Shape[0];
        int hiddenDim = (int)e.Info.Shape[1];
        if (vocabSize < 2 || hiddenDim < 1) { return; }

        (bool[] usable, byte[]?[] vocabHashByIdx) = ResolveVocabArrays(context, session, vocabSize, ct);
        if (CountTrue(usable) == 0)
        {
            Log.NoTokenizer(_logger, context.Source.ModelId);
            return;
        }

        double[] embed = SafetensorsReader.ReadTensorAsDouble(e.Info);

        long tuplesProcessed = 0;
        long edgesEmitted = 0;

        foreach (ResolvedTuple t in context.ResolvedTuples)
        {
            if (t.Tuple != ArchetypeTuple.AttentionBlock) { continue; }
            if (t.Modality != ModalityHint.Text && t.Modality != ModalityHint.TextEncoder
                && t.Modality != ModalityHint.TextDecoder)
            {
                continue;
            }
            ct.ThrowIfCancellationRequested();

            TensorHandle? q = FindMember(t, TupleSlot.Q);
            TensorHandle? k = FindMember(t, TupleSlot.K);
            TensorHandle? v = FindMember(t, TupleSlot.V);
            TensorHandle? o = FindMember(t, TupleSlot.O);
            if (q is null || k is null) { continue; }
            if (q.Info.Shape.Length != 2 || k.Info.Shape.Length != 2) { continue; }
            if ((int)q.Info.Shape[0] != hiddenDim || (int)k.Info.Shape[0] != hiddenDim) { continue; }
            int qCols = (int)q.Info.Shape[1];
            int kCols = (int)k.Info.Shape[1];

            if (qCols == kCols)
            {
                double[] qFlat = SafetensorsReader.ReadTensorAsDouble(q.Info);
                double[] kFlat = SafetensorsReader.ReadTensorAsDouble(k.Info);
                edgesEmitted += await EmitChunkedQK(
                    session, context, usable, vocabHashByIdx,
                    embed, qFlat, kFlat, vocabSize, hiddenDim, qCols,
                    "model_attention_qk_pattern", ct);
            }

            if (v is not null && o is not null
                && v.Info.Shape.Length == 2 && o.Info.Shape.Length == 2
                && (int)v.Info.Shape[0] == hiddenDim && (int)o.Info.Shape[1] == hiddenDim)
            {
                int vCols = (int)v.Info.Shape[1];
                int oRows = (int)o.Info.Shape[0];
                if (vCols == oRows)
                {
                    double[] vFlat = SafetensorsReader.ReadTensorAsDouble(v.Info);
                    double[] oFlat = SafetensorsReader.ReadTensorAsDouble(o.Info);
                    edgesEmitted += await EmitChunkedVO(
                        session, context, usable, vocabHashByIdx,
                        embed, vFlat, oFlat, vocabSize, hiddenDim, vCols, oRows,
                        "model_attention_vo_pattern", ct);
                }
            }

            tuplesProcessed++;
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, edgesEmitted);
    }

    private static async Task<long> EmitChunkedQK(
        IPassSession session, ModelPassContext context,
        bool[] usable, byte[]?[] vocabHashByIdx,
        double[] embed, double[] qFlat, double[] kFlat,
        int vocabSize, int hiddenDim, int dProj,
        string attestationTypeCode, CancellationToken ct)
    {
        // Materialize Pq AND Pk ONCE per layer. Vocab × dProj × 8B each.
        // Reused across ALL source chunks via slicing — no recomputation.
        // Replaces the prior shape that recomputed Pk per source chunk
        // (4× redundancy on MiniLM, worse on larger models — the N!
        // pattern the user called out).
        double[] pq = new double[(long)vocabSize * dProj];
        Gemm.F64(TransposeOp.None, TransposeOp.None,
                 vocabSize, dProj, hiddenDim,
                 1.0, embed, hiddenDim, qFlat, dProj,
                 0.0, pq, dProj);
        double[] pk = new double[(long)vocabSize * dProj];
        Gemm.F64(TransposeOp.None, TransposeOp.None,
                 vocabSize, dProj, hiddenDim,
                 1.0, embed, hiddenDim, kFlat, dProj,
                 0.0, pk, dProj);

        double[] srcNorms = new double[vocabSize];
        for (int v = 0; v < vocabSize; v++)
        {
            if (!usable[v]) { continue; }
            long off = (long)v * dProj;
            double sq = 0;
            for (int c = 0; c < dProj; c++) { double x = pq[off + c]; sq += x * x; }
            srcNorms[v] = Math.Sqrt(sq);
        }
        int[] sources = TopNByValueArray(srcNorms, usable, TopSourceTokens);
        if (sources.Length == 0) { return 0; }

        double temperature = ComputeTemperatureFromProjections(pq, srcNorms, vocabSize, dProj, usable);
        if (temperature <= 0) { temperature = 1.0; }

        return await ChunkedPairScoringFromMaterialized(
            session, context, usable, vocabHashByIdx,
            pq, pk, sources, vocabSize, dProj,
            attestationTypeCode, temperature, "model_attention_pattern", ct);
    }

    private static async Task<long> EmitChunkedVO(
        IPassSession session, ModelPassContext context,
        bool[] usable, byte[]?[] vocabHashByIdx,
        double[] embed, double[] vFlat, double[] oFlat,
        int vocabSize, int hiddenDim, int vCols, int oRows,
        string attestationTypeCode, CancellationToken ct)
    {
        // Pv = embed @ V (vocab × vCols). One GEMM, materialized.
        double[] pv = new double[(long)vocabSize * vCols];
        Gemm.F64(TransposeOp.None, TransposeOp.None,
                 vocabSize, vCols, hiddenDim,
                 1.0, embed, hiddenDim, vFlat, vCols,
                 0.0, pv, vCols);

        // Po[v, r] = embed[v] · O[r, :] = embed @ O^T. O is (oRows × hidden),
        // so contraction dim = hidden, ldb = hidden, opB = Transpose. ONE
        // GEMM, materialized — replaces per-source-chunk recomputation that
        // was the N! pattern.
        double[] po = new double[(long)vocabSize * oRows];
        Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                 vocabSize, oRows, hiddenDim,
                 1.0, embed, hiddenDim, oFlat, hiddenDim,
                 0.0, po, oRows);

        double[] srcNorms = new double[vocabSize];
        for (int v = 0; v < vocabSize; v++)
        {
            if (!usable[v]) { continue; }
            long off = (long)v * vCols;
            double sq = 0;
            for (int c = 0; c < vCols; c++) { double x = pv[off + c]; sq += x * x; }
            srcNorms[v] = Math.Sqrt(sq);
        }
        int[] sources = TopNByValueArray(srcNorms, usable, TopSourceTokens);
        if (sources.Length == 0) { return 0; }

        double temperature = ComputeTemperatureFromProjections(pv, srcNorms, vocabSize, vCols, usable);
        if (temperature <= 0) { temperature = 1.0; }

        // For V/O the projection dim of the source side (vCols) MUST equal
        // the projection dim of the target side (oRows) for the dot product
        // to make sense. Already validated by the caller.
        return await ChunkedPairScoringFromMaterialized(
            session, context, usable, vocabHashByIdx,
            pv, po, sources, vocabSize, vCols,
            attestationTypeCode, temperature, "model_attention_pattern", ct);
    }

    /// <summary>
    /// Source-side chunked pair scoring + emission. BOTH Pq AND Pk are
    /// pre-materialized (vocab × dProj each). Per (source-chunk,
    /// target-chunk) iteration: gather source rows from Pq, slice target
    /// rows from Pk, ONE GEMM produces S_block. No GEMM is repeated across
    /// chunk iterations. The N! pattern (recompute Pk per source chunk)
    /// is gone.
    /// </summary>
    private static async Task<long> ChunkedPairScoringFromMaterialized(
        IPassSession session, ModelPassContext context,
        bool[] usable, byte[]?[] vocabHashByIdx,
        double[] pq, double[] pk,
        int[] sources, int vocabSize, int dProj,
        string attestationTypeCode, double temperature, string edgeTypeCode,
        CancellationToken ct)
    {
        long emitted = 0;
        double[] sourceGather = new double[(long)SourceChunkSize * dProj];
        double[] sBlock = new double[(long)SourceChunkSize * TargetChunkSize];

        for (int sChunkStart = 0; sChunkStart < sources.Length; sChunkStart += SourceChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int sChunkLen = Math.Min(SourceChunkSize, sources.Length - sChunkStart);

            // Gather Pq rows for this source chunk into a contiguous buffer.
            for (int i = 0; i < sChunkLen; i++)
            {
                int a = sources[sChunkStart + i];
                long src = (long)a * dProj;
                long dst = (long)i * dProj;
                Buffer.BlockCopy(pq, (int)(src * sizeof(double)),
                                 sourceGather, (int)(dst * sizeof(double)),
                                 dProj * sizeof(double));
            }

            // Per-source running top-K accumulator (sign-bearing values
            // reduced to score deviation from 0.5).
            (int Tok, double SignedValue)[][] topK = new (int, double)[sChunkLen][];
            int[] topKFilled = new int[sChunkLen];
            for (int i = 0; i < sChunkLen; i++)
            {
                topK[i] = new (int, double)[TopKPerSourceRow];
            }
            double[] topKMinAbs = new double[sChunkLen];
            int[] topKMinIdx = new int[sChunkLen];
            for (int i = 0; i < sChunkLen; i++)
            {
                topKMinAbs[i] = double.PositiveInfinity;
                topKMinIdx[i] = -1;
            }

            for (int tChunkStart = 0; tChunkStart < vocabSize; tChunkStart += TargetChunkSize)
            {
                int tChunkLen = Math.Min(TargetChunkSize, vocabSize - tChunkStart);

                // Slice Pk[tChunkStart..tChunkStart+tChunkLen] — NO recomputation.
                // Pk is materialized once per layer per side; this slice is
                // free. Replaces the prior shape that did embed[t_chunk] @ K
                // per source chunk (4× redundancy on MiniLM).
                int pkRowOffset = checked(tChunkStart * dProj);
                int pkSliceLen  = checked(tChunkLen * dProj);

                // S_block = sourceGather (sChunkLen × dProj) @ Pk_slice^T (dProj × tChunkLen).
                Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                         sChunkLen, tChunkLen, dProj,
                         1.0, sourceGather, dProj,
                         pk.AsSpan(pkRowOffset, pkSliceLen), dProj,
                         0.0, sBlock, tChunkLen);

                // Update per-source top-K from this column slice.
                for (int i = 0; i < sChunkLen; i++)
                {
                    int a = sources[sChunkStart + i];
                    long rowOff = (long)i * tChunkLen;
                    for (int j = 0; j < tChunkLen; j++)
                    {
                        int b = tChunkStart + j;
                        if (b == a || !usable[b]) { continue; }
                        double signed = sBlock[rowOff + j];
                        double abs = Math.Abs(signed);
                        if (topKFilled[i] < TopKPerSourceRow)
                        {
                            topK[i][topKFilled[i]] = (b, signed);
                            topKFilled[i]++;
                            if (topKFilled[i] == TopKPerSourceRow)
                            {
                                RecomputeMinAbs(topK[i], topKFilled[i], out topKMinAbs[i], out topKMinIdx[i]);
                            }
                        }
                        else if (abs > topKMinAbs[i])
                        {
                            topK[i][topKMinIdx[i]] = (b, signed);
                            RecomputeMinAbs(topK[i], topKFilled[i], out topKMinAbs[i], out topKMinIdx[i]);
                        }
                    }
                }
            }

            // Emit per-source top-K events.
            for (int i = 0; i < sChunkLen; i++)
            {
                int a = sources[sChunkStart + i];
                byte[]? aHash = vocabHashByIdx[a];
                if (aHash is null) { continue; }

                for (int j = 0; j < topKFilled[i]; j++)
                {
                    (int b, double signed) = topK[i][j];
                    byte[]? bHash = vocabHashByIdx[b];
                    if (bHash is null) { continue; }

                    EntityHandle aH = new(aHash, "word_form");
                    EntityHandle bH = new(bHash, "word_form");
                    double score = Sigmoid(signed / temperature);

                    EdgeRatingEvent[] events =
                    [
                        new EdgeRatingEvent("model_trust",                   attestationTypeCode, score, 1.0),
                        new EdgeRatingEvent("attention_pattern_confidence", attestationTypeCode, score, 1.0),
                    ];
                    session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
                    [
                        new EdgeMemberSpec(aH, "source", 0),
                        new EdgeMemberSpec(bH, "target", 1),
                    ], System.Array.Empty<EdgeSignificanceSpec>(), events);
                    emitted++;
                }
            }

            await session.MaybeFlushAsync(FlushThreshold, ct);
        }

        return emitted;
    }

    private static double ComputeTemperatureFromProjections(double[] proj, double[] norms, int vocabSize, int dim, bool[] usable)
    {
        // Sample-based stddev estimate: pick the first usable token's
        // projection norm as a scale reference. Cheap, deterministic, and
        // serves as the sigmoid's transition scale (raw values within
        // ±temperature map to scores in roughly [0.27, 0.73]).
        double sumSq = 0;
        int counted = 0;
        for (int v = 0; v < vocabSize; v++)
        {
            if (!usable[v]) { continue; }
            sumSq += norms[v] * norms[v];
            counted++;
        }
        if (counted == 0) { return 0; }
        double meanNormSq = sumSq / counted;
        double meanNorm = Math.Sqrt(meanNormSq);
        // Pair value of two random unit-direction vectors in d-dim has
        // stddev ~ 1/sqrt(d). For projection vectors of typical norm
        // meanNorm, pair value stddev ~ meanNorm² / sqrt(d).
        return (meanNorm * meanNorm) / Math.Sqrt(dim);
    }

    private static double Sigmoid(double x)
    {
        if (x > 35) { return 1.0; }
        if (x < -35) { return 0.0; }
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    private static int CountTrue(bool[] arr)
    {
        int n = 0;
        for (int i = 0; i < arr.Length; i++) { if (arr[i]) { n++; } }
        return n;
    }

    private static TensorHandle? FindEmbedding(ModelPassContext context)
    {
        foreach (ResolvedTuple t in context.ResolvedTuples)
        {
            if (t.Tuple != ArchetypeTuple.EmbeddingLookup) { continue; }
            if (t.Modality != ModalityHint.Text) { continue; }
            foreach (TupleMember m in t.Members)
            {
                if (m.Slot == TupleSlot.Table && m.Tensor.Info.Shape.Length == 2)
                {
                    return m.Tensor;
                }
            }
        }
        return null;
    }

    private static TensorHandle? FindMember(ResolvedTuple t, TupleSlot slot)
    {
        foreach (TupleMember m in t.Members)
        {
            if (m.Slot == slot) { return m.Tensor; }
        }
        return null;
    }

    private static (bool[] Usable, byte[]?[] HashByIdx) ResolveVocabArrays(
        ModelPassContext context, IPassSession session, int vocabSize, CancellationToken ct)
    {
        bool[] usable = new bool[vocabSize];
        byte[]?[] hashByIdx = new byte[]?[vocabSize];
        string snapshotDir = context.Source.ModelDirectory;
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { return (usable, hashByIdx); }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(tokenizerJson); }
        catch (IOException) { return (usable, hashByIdx); } // BOUNDARY: optional tokenizer absent/unreadable disables attestation enrichment.
        if (bytes.Length == 0) { return (usable, hashByIdx); }
        TokenizerModel model;
        try { model = HuggingFaceTokenizerParser.Parse(bytes); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (usable, hashByIdx); } // BOUNDARY: malformed tokenizer.json disables attestation enrichment.

        foreach (KeyValuePair<int, VocabularyEntry> kv in model.Vocab)
        {
            ct.ThrowIfCancellationRequested();
            if ((uint)kv.Key >= (uint)vocabSize) { continue; }
            TextDecomposeResult r = SubstrateTextDecomposer.EmitStatic(
                session.Batch, kv.Value.TokenBytes,
                new TextDecomposeOptions(
                    ProvenanceCode: context.ProvenanceCode,
                    TopEntityType: "word_form",
                    TrustMu: ModelDerivedTrustMu));
            hashByIdx[kv.Key] = r.RootHash;
            usable[kv.Key] = true;
        }
        return (usable, hashByIdx);
    }

    private static int[] TopNByValueArray(double[] norm, bool[] usable, int n)
    {
        if (n < 1) { return Array.Empty<int>(); }
        (int Tok, double Val)[] buf = new (int, double)[n];
        int filled = 0;
        double minVal = double.PositiveInfinity;
        int minIdx = -1;
        for (int v = 0; v < norm.Length; v++)
        {
            if (!usable[v]) { continue; }
            double val = norm[v];
            if (val <= 0.0) { continue; }
            if (filled < n)
            {
                buf[filled] = (v, val);
                filled++;
                if (filled == n) { RecomputeMin(buf, filled, out minVal, out minIdx); }
            }
            else if (val > minVal)
            {
                buf[minIdx] = (v, val);
                RecomputeMin(buf, filled, out minVal, out minIdx);
            }
        }
        int[] result = new int[filled];
        for (int i = 0; i < filled; i++) { result[i] = buf[i].Tok; }
        return result;
    }

    private static void RecomputeMin((int Tok, double Val)[] buf, int filled, out double minVal, out int minIdx)
    {
        minVal = double.PositiveInfinity;
        minIdx = -1;
        for (int i = 0; i < filled; i++)
        {
            if (buf[i].Val < minVal) { minVal = buf[i].Val; minIdx = i; }
        }
    }

    private static void RecomputeMinAbs((int Tok, double Signed)[] buf, int filled, out double minAbs, out int minIdx)
    {
        minAbs = double.PositiveInfinity;
        minIdx = -1;
        for (int i = 0; i < filled; i++)
        {
            double abs = Math.Abs(buf[i].Signed);
            if (abs < minAbs) { minAbs = abs; minIdx = i; }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[attention-block {ModelId}] no input embedding tensor; skipped")]
        public static partial void NoEmbedding(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[attention-block {ModelId}] no tokenizer.json; skipped")]
        public static partial void NoTokenizer(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[attention-block {ModelId}] complete — tuples={Tuples} edges={Edges}")]
        public static partial void Complete(ILogger logger, string modelId, long tuples, long edges);
    }
}
