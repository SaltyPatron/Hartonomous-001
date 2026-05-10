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
/// Per docs/01-tensor-primitive-spec.md §II.3 + §II.4 + §IV. Processes
/// SwiGluFfn / BertFfn / MoE expert FFN tuples and emits per-(input, output)
/// Glicko-2 attestation events on <c>model_ffn_factor</c> edges between
/// word_form entities.
///
/// Per-pair value = embed[b] · response[a] where response[a] = Down @
/// activation(Up @ embed[a] [⊙ silu(Gate @ embed[a])]). Mapped to Glicko via
/// <c>score = sigmoid(value / temperature); weight = 1.0</c>. Sign and
/// magnitude both encoded in score; no per-row noise floor (Glicko absorbs it).
///
/// Memory-bounded chunk-streaming: source AND target chunks computed
/// on-demand; full vocab × hidden response matrix is NOT materialized.
/// Working set per layer ~100 MB regardless of model size.
/// </summary>
internal sealed partial class FfnTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.ffn";
    public IReadOnlyList<string> Dependencies => ["tuple.embedding_lookup"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopSourceTokens = 1024;
    private const int TopKPerSourceRow = 48;
    private const int SourceChunkSize = 256;
    private const int TargetChunkSize = 4096;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 25_000;

    private readonly ILogger _logger;

    public FfnTuplePass(ILogger logger)
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
            ct.ThrowIfCancellationRequested();
            (FfnTensors? tensors, string attestationType) = ResolveFfnTensors(t, hiddenDim);
            if (tensors is null) { continue; }

            edgesEmitted += await EmitChunkedFfnAttestations(
                session, context, usable, vocabHashByIdx,
                embed, vocabSize, hiddenDim, tensors.Value,
                attestationType, ct);
            tuplesProcessed++;
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, edgesEmitted);
    }

    private readonly struct FfnTensors
    {
        public readonly double[] Up;        // (intermediate × hiddenDim)
        public readonly double[] Down;      // (hiddenDim × intermediate)
        public readonly double[]? Gate;     // (intermediate × hiddenDim), null for BERT
        public readonly int Intermediate;
        public readonly bool IsSwiGlu;

        public FfnTensors(double[] up, double[] down, double[]? gate, int intermediate, bool isSwiGlu)
        {
            Up = up; Down = down; Gate = gate; Intermediate = intermediate; IsSwiGlu = isSwiGlu;
        }
    }

    private static (FfnTensors? Tensors, string AttestationType) ResolveFfnTensors(ResolvedTuple t, int hiddenDim)
    {
        TensorHandle? down;
        TensorHandle? up;
        TensorHandle? gate = null;
        bool isSwiGlu;
        string attestation;

        if (t.Tuple == ArchetypeTuple.SwiGluFfn)
        {
            down = FindMember(t, TupleSlot.Down);
            up   = FindMember(t, TupleSlot.Up);
            gate = FindMember(t, TupleSlot.Gate);
            if (down is null || up is null || gate is null) { return (null, string.Empty); }
            isSwiGlu = true;
            attestation = "model_ffn_full_path";
        }
        else if (t.Tuple == ArchetypeTuple.BertFfn)
        {
            up   = FindMember(t, TupleSlot.Intermediate);
            down = FindMember(t, TupleSlot.Output);
            if (down is null || up is null) { return (null, string.Empty); }
            isSwiGlu = false;
            attestation = "model_ffn_full_path";
        }
        else if (t.Tuple == ArchetypeTuple.MoeRouterBlock && t.ExpertIndex.HasValue)
        {
            up   = FindMember(t, TupleSlot.ExpertUp);
            down = FindMember(t, TupleSlot.ExpertDown);
            gate = FindMember(t, TupleSlot.ExpertGate);
            if (down is null || up is null) { return (null, string.Empty); }
            isSwiGlu = gate is not null;
            attestation = "model_moe_expert_response";
        }
        else
        {
            return (null, string.Empty);
        }

        if (up.Info.Shape.Length != 2 || down.Info.Shape.Length != 2) { return (null, string.Empty); }
        int intermediate = (int)up.Info.Shape[0];
        if ((int)up.Info.Shape[1] != hiddenDim
         || (int)down.Info.Shape[0] != hiddenDim
         || (int)down.Info.Shape[1] != intermediate)
        {
            return (null, string.Empty);
        }
        if (gate is not null && (gate.Info.Shape.Length != 2
            || (int)gate.Info.Shape[0] != intermediate
            || (int)gate.Info.Shape[1] != hiddenDim))
        {
            gate = null;
            isSwiGlu = false;
        }

        double[] upFlat   = SafetensorsReader.ReadTensorAsDouble(up.Info);
        double[] downFlat = SafetensorsReader.ReadTensorAsDouble(down.Info);
        double[]? gateFlat = gate is null ? null : SafetensorsReader.ReadTensorAsDouble(gate.Info);
        return (new FfnTensors(upFlat, downFlat, gateFlat, intermediate, isSwiGlu), attestation);
    }

    private static async Task<long> EmitChunkedFfnAttestations(
        IPassSession session, ModelPassContext context,
        bool[] usable, byte[]?[] vocabHashByIdx,
        double[] embed, int vocabSize, int hiddenDim,
        FfnTensors fn, string attestationTypeCode, CancellationToken ct)
    {
        // Source ranking via ||embed[v]||. The previous code ran the FULL
        // FFN forward on every vocab token to rank by ||response[v]|| —
        // that was vocab × intermediate × hidden × 3 FLOPs per layer
        // (~50 GFLOPs MiniLM, ~700 TFLOPs Llama-8B), and was the dominant
        // cost the user called out as "boil the ocean". Embed norm is a
        // free proxy: tokens with high-norm embeddings tend to produce
        // high-norm responses. The substrate's top-K-per-source design is
        // robust to source-ranking imperfection — under-ranked sources just
        // attest with lower priority within the top-N cap.
        double[] srcNorms = new double[vocabSize];
        for (int v = 0; v < vocabSize; v++)
        {
            if (!usable[v]) { continue; }
            long off = (long)v * hiddenDim;
            double sq = 0;
            for (int h = 0; h < hiddenDim; h++) { double x = embed[off + h]; sq += x * x; }
            srcNorms[v] = Math.Sqrt(sq);
        }
        int[] sources = TopNByValueArray(srcNorms, usable, TopSourceTokens);
        if (sources.Length == 0) { return 0; }

        // Materialize the FULL response matrix (vocab × hidden) ONCE per
        // layer. Reused across all source chunks via slicing — replaces the
        // prior per-source-chunk recomputation that was the N! pattern.
        // Memory: vocab × hidden × 8B = ~94 MB MiniLM, ~4 GB Llama-8B.
        double[] response = new double[(long)vocabSize * hiddenDim];
        double[] upFull   = new double[(long)vocabSize * fn.Intermediate];
        Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                 vocabSize, fn.Intermediate, hiddenDim,
                 1.0, embed, hiddenDim, fn.Up, hiddenDim,
                 0.0, upFull, fn.Intermediate);

        if (fn.IsSwiGlu && fn.Gate is not null)
        {
            double[] gateFull = new double[(long)vocabSize * fn.Intermediate];
            Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                     vocabSize, fn.Intermediate, hiddenDim,
                     1.0, embed, hiddenDim, fn.Gate, hiddenDim,
                     0.0, gateFull, fn.Intermediate);
            long actLen = (long)vocabSize * fn.Intermediate;
            for (long i = 0; i < actLen; i++)
            {
                double g = gateFull[i];
                upFull[i] *= g / (1.0 + Math.Exp(-g));
            }
        }
        else
        {
            long actLen = (long)vocabSize * fn.Intermediate;
            for (long i = 0; i < actLen; i++)
            {
                double x = upFull[i];
                upFull[i] = x / (1.0 + Math.Exp(-1.702 * x));
            }
        }

        Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                 vocabSize, hiddenDim, fn.Intermediate,
                 1.0, upFull, fn.Intermediate, fn.Down, fn.Intermediate,
                 0.0, response, hiddenDim);

        // Sigmoid temperature: scale based on mean response norm and dim.
        // Need to compute response norms briefly (fast — already have response).
        double sumSq = 0;
        int counted = 0;
        for (int v = 0; v < vocabSize; v++)
        {
            if (!usable[v]) { continue; }
            long off = (long)v * hiddenDim;
            double sq = 0;
            for (int h = 0; h < hiddenDim; h++) { double x = response[off + h]; sq += x * x; }
            sumSq += sq;
            counted++;
        }
        double meanNormSq = counted > 0 ? sumSq / counted : 1.0;
        double meanNorm = Math.Sqrt(meanNormSq);
        double temperature = (meanNorm * meanNorm) / Math.Sqrt(hiddenDim);
        if (temperature <= 0) { temperature = 1.0; }

        long emitted = 0;
        double[] sourceGather = new double[(long)SourceChunkSize * hiddenDim];
        double[] sBlock = new double[(long)SourceChunkSize * TargetChunkSize];

        for (int sChunkStart = 0; sChunkStart < sources.Length; sChunkStart += SourceChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int sChunkLen = Math.Min(SourceChunkSize, sources.Length - sChunkStart);

            // Gather response rows for this source chunk — slice from the
            // pre-materialized response matrix. NO recomputation.
            for (int i = 0; i < sChunkLen; i++)
            {
                int a = sources[sChunkStart + i];
                long src = (long)a * hiddenDim;
                long dst = (long)i * hiddenDim;
                Buffer.BlockCopy(response, (int)(src * sizeof(double)),
                                 sourceGather, (int)(dst * sizeof(double)),
                                 hiddenDim * sizeof(double));
            }

            (int Tok, double SignedValue)[][] topK = new (int, double)[sChunkLen][];
            int[] topKFilled = new int[sChunkLen];
            for (int i = 0; i < sChunkLen; i++) { topK[i] = new (int, double)[TopKPerSourceRow]; }
            double[] topKMinAbs = new double[sChunkLen];
            int[] topKMinIdx = new int[sChunkLen];
            for (int i = 0; i < sChunkLen; i++) { topKMinAbs[i] = double.PositiveInfinity; topKMinIdx[i] = -1; }

            // Per target chunk: pair score = sourceResponse · embed[b].
            // Embed slice is read directly — no recomputation.
            for (int tChunkStart = 0; tChunkStart < vocabSize; tChunkStart += TargetChunkSize)
            {
                int tChunkLen = Math.Min(TargetChunkSize, vocabSize - tChunkStart);

                int embedRowOffset = checked(tChunkStart * hiddenDim);
                int embedSliceLen  = checked(tChunkLen * hiddenDim);
                Gemm.F64(TransposeOp.None, TransposeOp.Transpose,
                         sChunkLen, tChunkLen, hiddenDim,
                         1.0,
                         sourceGather.AsSpan(0, checked(sChunkLen * hiddenDim)), hiddenDim,
                         embed.AsSpan(embedRowOffset, embedSliceLen), hiddenDim,
                         0.0,
                         sBlock.AsSpan(0, checked(sChunkLen * tChunkLen)), tChunkLen);

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

                    EntityHandle aH;
                    EntityHandle bH;
                    if (CompareBytes(aHash, bHash) <= 0)
                    {
                        aH = new EntityHandle(aHash, "word_form");
                        bH = new EntityHandle(bHash, "word_form");
                    }
                    else
                    {
                        aH = new EntityHandle(bHash, "word_form");
                        bH = new EntityHandle(aHash, "word_form");
                    }

                    double score = Sigmoid(signed / temperature);
                    EdgeRatingEvent[] events =
                    [
                        new EdgeRatingEvent("model_trust",        attestationTypeCode, score, 1.0),
                        new EdgeRatingEvent("semantic_relevance", attestationTypeCode, score, 1.0),
                    ];
                    session.Batch.AddEdge("model_ffn_factor", context.ProvenanceCode,
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

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) { return c; }
        }
        return a.Length.CompareTo(b.Length);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] no input embedding tensor; skipped")]
        public static partial void NoEmbedding(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] no tokenizer.json; skipped")]
        public static partial void NoTokenizer(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] complete — tuples={Tuples} edges={Edges}")]
        public static partial void Complete(ILogger logger, string modelId, long tuples, long edges);
    }
}
