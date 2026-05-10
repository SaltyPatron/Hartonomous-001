using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §II.1 + §IV. Processes AttentionBlock
/// tuples: reads Q + K (and V + O when present) tensors, projects through the
/// model's input embedding, and emits per-token-pair attestations on
/// <c>model_attention_pattern</c> edges between word_form entities. Same edge
/// identity stratified by attestation_type:
///   - <c>model_attention_qk_pattern</c> for the Q×K pair signal
///   - <c>model_attention_vo_pattern</c> for the V×O pair signal
///
/// Math (per layer, per head when head-decomposed):
///   1. Compute per-token Q response norm: ‖embed[v] · Q‖.
///   2. Compute per-token K response norm: ‖embed[v] · K‖.
///   3. Top-K-per-side above adaptive noise floor; for each (q_token, k_token):
///      emit edge model_attention_pattern(q_token, k_token) with
///      attestation_type=model_attention_qk_pattern, mu = clamp(1500 +
///      (qNorm × kNorm / scale) × 200, 500, 2500).
///   4. Same shape for V, O — different attestation_type.
///
/// Sign-blind per spec §V (post-softmax kills sign anyway). Magnitude → mu.
/// </summary>
internal sealed partial class AttentionBlockTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.attention_block";
    public IReadOnlyList<string> Dependencies => ["tuple.embedding_lookup"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerSide = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public AttentionBlockTuplePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        // Find the input embedding tensor — needed for Q/K/V/O projection-to-token math.
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

        Dictionary<int, byte[]>? vocabHashes = ResolveVocabHashes(context, session, ct);
        if (vocabHashes is null || vocabHashes.Count == 0)
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
            // Self-attention only here. Cross-attention has its own TuplePass that
            // binds Q-side and K/V-side to different content-entity types.
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

            double[] qFlat = SafetensorsReader.ReadTensorAsDouble(q.Info);
            double[] kFlat = SafetensorsReader.ReadTensorAsDouble(k.Info);
            int qCols = (int)q.Info.Shape[1];
            int kCols = (int)k.Info.Shape[1];

            // Q×K side
            double[] qNorm = ProjectedNormsByCols(embed, vocabSize, hiddenDim, qFlat, qCols);
            double[] kNorm = ProjectedNormsByCols(embed, vocabSize, hiddenDim, kFlat, kCols);
            edgesEmitted += await EmitPairAttestations(
                session, context, vocabHashes, qNorm, kNorm,
                "model_attention_qk_pattern", ct);

            // V×O side (when present)
            if (v is not null && o is not null
                && v.Info.Shape.Length == 2 && o.Info.Shape.Length == 2
                && (int)v.Info.Shape[0] == hiddenDim && (int)o.Info.Shape[1] == hiddenDim)
            {
                double[] vFlat = SafetensorsReader.ReadTensorAsDouble(v.Info);
                double[] oFlat = SafetensorsReader.ReadTensorAsDouble(o.Info);
                int vCols = (int)v.Info.Shape[1];
                int oRows = (int)o.Info.Shape[0];
                double[] vNorm = ProjectedNormsByCols(embed, vocabSize, hiddenDim, vFlat, vCols);
                double[] oNorm = ProjectedNormsByRowsTransposed(embed, vocabSize, hiddenDim, oFlat, oRows);
                edgesEmitted += await EmitPairAttestations(
                    session, context, vocabHashes, vNorm, oNorm,
                    "model_attention_vo_pattern", ct);
            }

            tuplesProcessed++;
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, edgesEmitted);
    }

    private static async Task<long> EmitPairAttestations(
        IPassSession session, ModelPassContext context,
        Dictionary<int, byte[]> vocabHashes,
        double[] sideA, double[] sideB,
        string attestationTypeCode,
        CancellationToken ct)
    {
        (double aFloor, double aMean) = NoiseStats(sideA, vocabHashes);
        (double bFloor, double bMean) = NoiseStats(sideB, vocabHashes);
        int[] topA = TopKByValue(sideA, vocabHashes, aFloor, TopKPerSide);
        int[] topB = TopKByValue(sideB, vocabHashes, bFloor, TopKPerSide);
        if (topA.Length == 0 || topB.Length == 0) { return 0; }
        double scale = aMean * bMean;
        if (scale <= 0) { scale = 1.0; }

        long emitted = 0;
        foreach (int aTok in topA)
        {
            foreach (int bTok in topB)
            {
                if (aTok == bTok) { continue; }
                if (!vocabHashes.TryGetValue(aTok, out byte[]? aHash) || aHash is null) { continue; }
                if (!vocabHashes.TryGetValue(bTok, out byte[]? bHash) || bHash is null) { continue; }

                EntityHandle aH = new(aHash, "word_form");
                EntityHandle bH = new(bHash, "word_form");
                double pairStrength = sideA[aTok] * sideB[bTok];
                double mu = Math.Clamp(1500.0 + (pairStrength / scale) * 200.0, 500.0, 2500.0);

                EdgeSignificanceSpec[] sig =
                [
                    new EdgeSignificanceSpec("model_trust", attestationTypeCode, mu),
                    new EdgeSignificanceSpec("attention_pattern_confidence", attestationTypeCode, mu),
                ];

                session.Batch.AddEdge("model_attention_pattern", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(aH, "source", 0),
                    new EdgeMemberSpec(bH, "target", 1),
                ], sig);
                emitted++;
                if (emitted % FlushThreshold == 0)
                {
                    await session.MaybeFlushAsync(FlushThreshold, ct);
                }
            }
        }
        return emitted;
    }

    private static TensorHandle? FindEmbedding(ModelPassContext context)
    {
        // Find the EmbeddingLookup tuple's table member with text modality.
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

    private static Dictionary<int, byte[]>? ResolveVocabHashes(
        ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        string snapshotDir = context.Source.ModelDirectory;
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { return null; }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(tokenizerJson); }
        catch (IOException) { return null; } // BOUNDARY: optional tokenizer absent/unreadable disables attestation enrichment for this pass.
        if (bytes.Length == 0) { return null; }
        TokenizerModel model;
        try { model = HuggingFaceTokenizerParser.Parse(bytes); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; } // BOUNDARY: malformed tokenizer.json disables attestation enrichment for this pass.

        Dictionary<int, byte[]> map = new(model.Vocab.Count);
        foreach (KeyValuePair<int, VocabularyEntry> kv in model.Vocab)
        {
            ct.ThrowIfCancellationRequested();
            TextDecomposeResult r = SubstrateTextDecomposer.EmitStatic(
                session.Batch, kv.Value.TokenBytes,
                new TextDecomposeOptions(
                    ProvenanceCode: context.ProvenanceCode,
                    TopEntityType: "word_form",
                    TrustMu: ModelDerivedTrustMu));
            map[kv.Key] = r.RootHash;
        }
        return map;
    }

    private static double[] ProjectedNormsByCols(
        double[] embed, int vocabSize, int hiddenDim,
        double[] projection, int projCols)
    {
        double[] norms = new double[vocabSize];
        for (int v = 0; v < vocabSize; v++)
        {
            long embOff = (long)v * hiddenDim;
            double sumSq = 0;
            for (int c = 0; c < projCols; c++)
            {
                double dot = 0;
                for (int i = 0; i < hiddenDim; i++)
                {
                    dot += embed[embOff + i] * projection[(long)i * projCols + c];
                }
                sumSq += dot * dot;
            }
            norms[v] = Math.Sqrt(sumSq);
        }
        return norms;
    }

    private static double[] ProjectedNormsByRowsTransposed(
        double[] embed, int vocabSize, int hiddenDim,
        double[] projection, int projRows)
    {
        double[] norms = new double[vocabSize];
        for (int v = 0; v < vocabSize; v++)
        {
            long embOff = (long)v * hiddenDim;
            double sumSq = 0;
            for (int r = 0; r < projRows; r++)
            {
                double dot = 0;
                long rowOff = (long)r * hiddenDim;
                for (int h = 0; h < hiddenDim; h++)
                {
                    dot += embed[embOff + h] * projection[rowOff + h];
                }
                sumSq += dot * dot;
            }
            norms[v] = Math.Sqrt(sumSq);
        }
        return norms;
    }

    private static (double Floor, double Mean) NoiseStats(double[] norms, Dictionary<int, byte[]> vocabHashes)
    {
        double sum = 0;
        int counted = 0;
        for (int v = 0; v < norms.Length; v++)
        {
            if (vocabHashes.ContainsKey(v) && norms[v] > 0)
            {
                sum += norms[v];
                counted++;
            }
        }
        double mean = counted > 0 ? sum / counted : 0;
        return (mean * NoiseFraction, mean);
    }

    private static int[] TopKByValue(double[] norm, Dictionary<int, byte[]> vocabHashes, double noiseFloor, int k)
    {
        if (k < 1) { return Array.Empty<int>(); }
        (int Tok, double Val)[] buf = new (int, double)[k];
        int filled = 0;
        double minVal = double.PositiveInfinity;
        int minIdx = -1;
        for (int v = 0; v < norm.Length; v++)
        {
            if (!vocabHashes.ContainsKey(v)) { continue; }
            double val = norm[v];
            if (val < noiseFloor) { continue; }
            if (filled < k)
            {
                buf[filled] = (v, val);
                filled++;
                if (filled == k)
                {
                    RecomputeMin(buf, filled, out minVal, out minIdx);
                }
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
            if (buf[i].Val < minVal)
            {
                minVal = buf[i].Val;
                minIdx = i;
            }
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
