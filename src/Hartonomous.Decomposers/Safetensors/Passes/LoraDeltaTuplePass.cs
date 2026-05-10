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
/// Per docs/01-tensor-primitive-spec.md §II.6 + §IV. Processes LoraDelta
/// tuples: (base, A, B) where A·B is the rank-r delta over the base linear.
/// Computes the composed adaptation effect ΔW = B·A, projects against the
/// model's input embedding, and emits per-token-pair attestations on
/// <c>model_concept_similarity</c> edges with attestation_type
/// <c>model_lora_adapter_evidence</c>.
///
/// The base linear (q_proj, k_proj, ffn_down, etc.) ALSO produces its own
/// attestation via the AttentionBlock / Ffn TuplePass — the LoRA delta
/// stacks on the SAME edges with a distinct attestation_type so the
/// recomposer can choose to merge (apply delta to base) or keep separate
/// (export base + sibling adapter file).
/// </summary>
internal sealed partial class LoraDeltaTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.lora_delta";
    public IReadOnlyList<string> Dependencies => ["tuple.embedding_lookup"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerSide = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public LoraDeltaTuplePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        TensorHandle? embeddingTensor = FindEmbedding(context);
        if (embeddingTensor is null) { return; }
        TensorHandle e = embeddingTensor;
        int vocabSize = (int)e.Info.Shape[0];
        int hiddenDim = (int)e.Info.Shape[1];
        if (vocabSize < 2 || hiddenDim < 1) { return; }

        Dictionary<int, byte[]>? vocabHashes = ResolveVocabHashes(context, session, ct);
        if (vocabHashes is null || vocabHashes.Count == 0) { return; }

        double[] embed = SafetensorsReader.ReadTensorAsDouble(e.Info);

        long tuplesProcessed = 0;
        long edgesEmitted = 0;

        foreach (ResolvedTuple t in context.ResolvedTuples)
        {
            if (t.Tuple != ArchetypeTuple.LoraDelta) { continue; }
            ct.ThrowIfCancellationRequested();

            TensorHandle? aT = FindMember(t, TupleSlot.LoraA);
            TensorHandle? bT = FindMember(t, TupleSlot.LoraB);
            if (aT is null || bT is null) { continue; }
            if (aT.Info.Shape.Length != 2 || bT.Info.Shape.Length != 2) { continue; }

            int aRows = (int)aT.Info.Shape[0];
            int aCols = (int)aT.Info.Shape[1];
            int bRows = (int)bT.Info.Shape[0];
            int bCols = (int)bT.Info.Shape[1];

            // PEFT convention: A=[rank, hidden_in], B=[hidden_out, rank]; ΔW=B·A=[hidden_out, hidden_in].
            // Diffusers convention: A=[hidden_in, rank], B=[rank, hidden_out].
            int rank;
            int hiddenIn;
            int hiddenOut;
            bool peftLayout;
            if (aRows == bCols)
            {
                rank = aRows; hiddenIn = aCols; hiddenOut = bRows; peftLayout = true;
            }
            else if (aCols == bRows)
            {
                rank = aCols; hiddenIn = aRows; hiddenOut = bCols; peftLayout = false;
            }
            else { continue; }
            if (hiddenIn != hiddenDim || hiddenOut != hiddenDim) { continue; }

            double[] aFlat = SafetensorsReader.ReadTensorAsDouble(aT.Info);
            double[] bFlat = SafetensorsReader.ReadTensorAsDouble(bT.Info);

            // Per-token magnitude of ΔW · embed[v] = ‖B · A · embed[v]‖.
            double[] response = new double[vocabSize];
            double sumAbs = 0.0;
            double[] z = new double[rank];
            for (int v = 0; v < vocabSize; v++)
            {
                long embOff = (long)v * hiddenDim;
                for (int r = 0; r < rank; r++)
                {
                    double dot = 0.0;
                    for (int hin = 0; hin < hiddenDim; hin++)
                    {
                        double aw = peftLayout
                            ? aFlat[(long)r * hiddenDim + hin]
                            : aFlat[(long)hin * rank + r];
                        dot += embed[embOff + hin] * aw;
                    }
                    z[r] = dot;
                }
                double sumSq = 0.0;
                for (int hout = 0; hout < hiddenDim; hout++)
                {
                    double dot = 0.0;
                    for (int r = 0; r < rank; r++)
                    {
                        double bw = peftLayout
                            ? bFlat[(long)hout * rank + r]
                            : bFlat[(long)r * hiddenDim + hout];
                        dot += bw * z[r];
                    }
                    sumSq += dot * dot;
                }
                double mag = Math.Sqrt(sumSq);
                response[v] = mag;
                sumAbs += mag;
            }
            double meanAbs = vocabSize > 0 ? sumAbs / vocabSize : 0.0;
            double noiseFloor = meanAbs * NoiseFraction;
            if (noiseFloor <= 0) { continue; }

            // Top-K above noise floor; emit pairs.
            int[] topTokens = TopKByValue(response, vocabHashes, noiseFloor, TopKPerSide);
            if (topTokens.Length < 2) { continue; }

            for (int i = 0; i < topTokens.Length; i++)
            {
                for (int j = i + 1; j < topTokens.Length; j++)
                {
                    byte[] hashA = vocabHashes[topTokens[i]];
                    byte[] hashB = vocabHashes[topTokens[j]];
                    EntityHandle aH;
                    EntityHandle bH;
                    if (CompareBytes(hashA, hashB) <= 0)
                    {
                        aH = new EntityHandle(hashA, "word_form");
                        bH = new EntityHandle(hashB, "word_form");
                    }
                    else
                    {
                        aH = new EntityHandle(hashB, "word_form");
                        bH = new EntityHandle(hashA, "word_form");
                    }

                    double pairAbs = (response[topTokens[i]] + response[topTokens[j]]) / 2.0;
                    double mu = Math.Clamp(1500.0 + ((pairAbs / Math.Max(meanAbs, 1e-12)) * 200.0), 500.0, 2500.0);

                    EdgeSignificanceSpec[] sig =
                    [
                        new EdgeSignificanceSpec("model_trust", "model_lora_adapter_evidence", mu),
                        new EdgeSignificanceSpec("semantic_relevance", "model_lora_adapter_evidence", mu),
                    ];

                    session.Batch.AddEdge("model_concept_similarity", context.ProvenanceCode,
                    [
                        new EdgeMemberSpec(aH, "source", 0),
                        new EdgeMemberSpec(bH, "target", 1),
                    ], sig);
                    edgesEmitted++;
                    if (edgesEmitted % FlushThreshold == 0)
                    {
                        await session.MaybeFlushAsync(FlushThreshold, ct);
                    }
                }
            }
            tuplesProcessed++;
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, edgesEmitted);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[lora-delta {ModelId}] complete — tuples={Tuples} edges={Edges}")]
        public static partial void Complete(ILogger logger, string modelId, long tuples, long edges);
    }
}
