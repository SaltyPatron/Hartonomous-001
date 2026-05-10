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
/// Per docs/01-tensor-primitive-spec.md §II.3 + §II.4 + §IV. Processes
/// SwiGluFfn (gate/up/down) and BertFfn (intermediate/output) tuples plus
/// MoE expert FFNs (per ResolvedTuple.ExpertIndex). Reads FFN_DOWN-equivalent
/// tensor (down for SwiGLU, output for BERT, expert_down for MoE expert),
/// projects each output direction against the input embedding to find which
/// tokens this FFN neuron treats as related, emits per-token-pair
/// <c>model_ffn_factor</c> edges with attestation_type
/// <c>model_ffn_full_path</c> (or <c>model_moe_expert_response</c> for MoE
/// expert tuples).
/// </summary>
internal sealed partial class FfnTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.ffn";
    public IReadOnlyList<string> Dependencies => ["tuple.embedding_lookup"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerNeuron = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

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
            ct.ThrowIfCancellationRequested();
            (TensorHandle? downTensor, string attestationType) = ResolveDownAndAttestation(t);
            if (downTensor is null) { continue; }
            if (downTensor.Info.Shape.Length != 2) { continue; }
            int rows = (int)downTensor.Info.Shape[0];
            int cols = (int)downTensor.Info.Shape[1];
            if (rows != hiddenDim) { continue; }

            double[] flat = SafetensorsReader.ReadTensorAsDouble(downTensor.Info);

            for (int colIdx = 0; colIdx < cols; colIdx++)
            {
                ct.ThrowIfCancellationRequested();

                // Project this neuron's residual contribution against embedding.
                double[] response = new double[vocabSize];
                double sumAbs = 0.0;
                for (int v = 0; v < vocabSize; v++)
                {
                    long embOff = (long)v * hiddenDim;
                    double dot = 0.0;
                    for (int h = 0; h < rows; h++)
                    {
                        dot += embed[embOff + h] * flat[(long)h * cols + colIdx];
                    }
                    response[v] = dot;
                    sumAbs += Math.Abs(dot);
                }
                double meanAbs = vocabSize > 0 ? sumAbs / vocabSize : 0.0;
                double noiseFloor = meanAbs * NoiseFraction;
                if (noiseFloor <= 0) { continue; }

                (int Tok, double Resp, double Abs)[] top = new (int, double, double)[TopKPerNeuron];
                int filled = 0;
                double minAbs = double.PositiveInfinity;
                int minIdx = -1;
                for (int v = 0; v < vocabSize; v++)
                {
                    if (!vocabHashes.ContainsKey(v)) { continue; }
                    double abs = Math.Abs(response[v]);
                    if (abs < noiseFloor) { continue; }
                    if (filled < TopKPerNeuron)
                    {
                        top[filled] = (v, response[v], abs);
                        filled++;
                        if (filled == TopKPerNeuron)
                        {
                            RecomputeMin(top, filled, out minAbs, out minIdx);
                        }
                    }
                    else if (abs > minAbs)
                    {
                        top[minIdx] = (v, response[v], abs);
                        RecomputeMin(top, filled, out minAbs, out minIdx);
                    }
                }
                if (filled < 2) { continue; }

                double absSum = 0;
                for (int kk = 0; kk < filled; kk++) { absSum += top[kk].Abs; }
                double absMean = absSum / filled;

                for (int i = 0; i < filled; i++)
                {
                    for (int j = i + 1; j < filled; j++)
                    {
                        byte[] hashA = vocabHashes[top[i].Tok];
                        byte[] hashB = vocabHashes[top[j].Tok];
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
                        double pairAbs = (top[i].Abs + top[j].Abs) / 2.0;
                        double mu = Math.Clamp(1500.0 + ((pairAbs / Math.Max(absMean, 1e-12)) * 200.0), 500.0, 2500.0);

                        EdgeSignificanceSpec[] sig =
                        [
                            new EdgeSignificanceSpec("model_trust", attestationType, mu),
                            new EdgeSignificanceSpec("semantic_relevance", attestationType, mu),
                        ];
                        session.Batch.AddEdge("model_ffn_factor", context.ProvenanceCode,
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
            }
            tuplesProcessed++;
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, edgesEmitted);
    }

    /// <summary>
    /// Returns (down-equivalent tensor, attestation_type) per tuple shape.
    /// SwiGluFfn → Down + model_ffn_full_path.
    /// BertFfn → Output + model_ffn_full_path.
    /// MoeRouterBlock per-expert → ExpertDown + model_moe_expert_response.
    /// Other tuples → (null, "").
    /// </summary>
    private static (TensorHandle? Tensor, string AttestationType) ResolveDownAndAttestation(ResolvedTuple t)
    {
        if (t.Tuple == ArchetypeTuple.SwiGluFfn)
        {
            return (FindMember(t, TupleSlot.Down), "model_ffn_full_path");
        }
        if (t.Tuple == ArchetypeTuple.BertFfn)
        {
            return (FindMember(t, TupleSlot.Output), "model_ffn_full_path");
        }
        if (t.Tuple == ArchetypeTuple.MoeRouterBlock && t.ExpertIndex.HasValue)
        {
            return (FindMember(t, TupleSlot.ExpertDown), "model_moe_expert_response");
        }
        return (null, string.Empty);
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

    private static void RecomputeMin((int Tok, double Resp, double Abs)[] buf, int filled,
        out double minAbs, out int minIdx)
    {
        minAbs = double.PositiveInfinity;
        minIdx = -1;
        for (int i = 0; i < filled; i++)
        {
            if (buf[i].Abs < minAbs)
            {
                minAbs = buf[i].Abs;
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] no input embedding tensor; skipped")]
        public static partial void NoEmbedding(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] no tokenizer.json; skipped")]
        public static partial void NoTokenizer(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-tuple {ModelId}] complete — tuples={Tuples} edges={Edges}")]
        public static partial void Complete(ILogger logger, string modelId, long tuples, long edges);
    }
}
