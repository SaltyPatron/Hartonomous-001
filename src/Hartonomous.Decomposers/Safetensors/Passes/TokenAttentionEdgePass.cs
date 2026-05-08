using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Direct token↔token edge emission from attention Q/K matrices via projection
/// through the input embedding. Completes the trio with
/// <see cref="TokenCrossEdgePass"/> (embedding cosine) and
/// <see cref="TokenFfnEdgePass"/> (FFN factor).
///
/// Per layer (paired Q and K tensors):
///   1. Compute per-token Q response norm: ‖embed[v] · Q‖.
///   2. Compute per-token K response norm: ‖embed[v] · K‖.
///   3. Top-K tokens per side. For each (q_token, k_token) pair: emit edge
///      <c>model_attention_pattern(q_token, k_token)</c> with
///      <c>attestation_type = model_attention_qk_pattern</c>, mu from
///      ‖q_query‖ × ‖k_key‖ scaled against per-tensor mean.
///
/// Per-head decomposition (one attestation per head per pair) would refine
/// further but TensorClassification doesn't currently surface NumHeads;
/// this layer-aggregated form is a strict superset of "this layer's
/// attention reads from token A and produces key responses for token B."
/// </summary>
internal sealed partial class TokenAttentionEdgePass : IModelAnalysisPass
{
    public string PassId => "model.token_attention_edges";
    public IReadOnlyList<string> Dependencies => ["model.tokenizer_mapping"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerSide = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public TokenAttentionEdgePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        TensorHandle? embeddingTensor = null;
        foreach (TensorHandle t in context.Tensors)
        {
            if (t.Classification.Role == TensorRole.TokenEmbedding && t.Info.Shape.Length == 2)
            {
                embeddingTensor = t;
                break;
            }
        }
        if (embeddingTensor is null)
        {
            Log.NoEmbedding(_logger, context.Source.ModelId);
            return;
        }
        TensorHandle e = embeddingTensor;
        int vocabSize = (int)e.Info.Shape[0];
        int hiddenDim = (int)e.Info.Shape[1];
        if (vocabSize < 2 || hiddenDim < 1)
        {
            return;
        }

        double[] embed = SafetensorsReader.ReadTensorAsDouble(e.Info);

        Dictionary<int, byte[]>? vocabHashes = TryBuildVocabTokenHashMap(context, session, ct);
        if (vocabHashes is null || vocabHashes.Count == 0)
        {
            Log.NoTokenizerMap(_logger, context.Source.ModelId);
            return;
        }

        Dictionary<int, TensorHandle> qByLayer = new();
        Dictionary<int, TensorHandle> kByLayer = new();
        foreach (TensorHandle t in context.Tensors)
        {
            if (t.Info.Shape.Length != 2)
            {
                continue;
            }
            int layer = t.Classification.LayerIndex ?? -1;
            if (layer < 0)
            {
                continue;
            }
            if (t.Classification.Role == TensorRole.AttentionQuery)
            {
                qByLayer[layer] = t;
            }
            else if (t.Classification.Role == TensorRole.AttentionKey)
            {
                kByLayer[layer] = t;
            }
        }

        long edgesEmitted = 0;
        long layersProcessed = 0;

        foreach ((int layer, TensorHandle qT) in qByLayer)
        {
            ct.ThrowIfCancellationRequested();
            if (!kByLayer.TryGetValue(layer, out TensorHandle? kT) || kT is null)
            {
                continue;
            }

            int qRows = (int)qT.Info.Shape[0];
            int qCols = (int)qT.Info.Shape[1];
            int kRows = (int)kT.Info.Shape[0];
            int kCols = (int)kT.Info.Shape[1];
            if (qRows != hiddenDim || kRows != hiddenDim)
            {
                continue;
            }

            double[] qFlat = SafetensorsReader.ReadTensorAsDouble(qT.Info);
            double[] kFlat = SafetensorsReader.ReadTensorAsDouble(kT.Info);

            double[] qNorm = ComputeProjectedNorms(embed, vocabSize, hiddenDim, qFlat, qCols);
            double[] kNorm = ComputeProjectedNorms(embed, vocabSize, hiddenDim, kFlat, kCols);

            (double qFloor, double qMean) = NoiseStats(qNorm, vocabHashes);
            (double kFloor, double kMean) = NoiseStats(kNorm, vocabHashes);

            int[] qTopTokens = TopKByValue(qNorm, vocabHashes, qFloor, TopKPerSide);
            int[] kTopTokens = TopKByValue(kNorm, vocabHashes, kFloor, TopKPerSide);
            if (qTopTokens.Length == 0 || kTopTokens.Length == 0)
            {
                continue;
            }

            double scale = qMean * kMean;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            foreach (int qTok in qTopTokens)
            {
                foreach (int kTok in kTopTokens)
                {
                    if (qTok == kTok)
                    {
                        continue;
                    }
                    if (!vocabHashes.TryGetValue(qTok, out byte[]? qHash) || qHash is null) { continue; }
                    if (!vocabHashes.TryGetValue(kTok, out byte[]? kHash) || kHash is null) { continue; }

                    EntityHandle qH = new(qHash, "word_form");
                    EntityHandle kH = new(kHash, "word_form");

                    double pairStrength = qNorm[qTok] * kNorm[kTok];
                    double mu = Math.Clamp(1500.0 + (pairStrength / scale) * 200.0, 500.0, 2500.0);

                    EdgeSignificanceSpec[] sigSpecs =
                    [
                        new EdgeSignificanceSpec("model_trust", "model_attention_qk_pattern", mu),
                        new EdgeSignificanceSpec("attention_pattern_confidence", "model_attention_qk_pattern", mu),
                    ];

                    session.Batch.AddEdge(
                        "model_attention_pattern",
                        context.ProvenanceCode,
                        [
                            new EdgeMemberSpec(qH, "source", 0),
                            new EdgeMemberSpec(kH, "target", 1),
                        ],
                        sigSpecs);
                    edgesEmitted++;
                    if (edgesEmitted % FlushThreshold == 0)
                    {
                        await session.MaybeFlushAsync(FlushThreshold, ct);
                    }
                }
            }
            layersProcessed++;
        }

        Log.PassComplete(_logger, context.Source.ModelId, layersProcessed, edgesEmitted);
    }

    private static double[] ComputeProjectedNorms(
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

    private static (double Floor, double Mean) NoiseStats(
        double[] norms, Dictionary<int, byte[]> vocabHashes)
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

    private static int[] TopKByValue(
        double[] norm,
        Dictionary<int, byte[]> vocabHashes,
        double noiseFloor,
        int k)
    {
        if (k < 1) { return Array.Empty<int>(); }
        (int Tok, double Val)[] buf = new (int, double)[k];
        int filled = 0;
        double minVal = double.PositiveInfinity;
        int minIdx = -1;
        for (int v = 0; v < norm.Length; v++)
        {
            if (!vocabHashes.ContainsKey(v))
            {
                continue;
            }
            double val = norm[v];
            if (val < noiseFloor)
            {
                continue;
            }
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

    private static void RecomputeMin(
        (int Tok, double Val)[] buf, int filled, out double minVal, out int minIdx)
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

    private static Dictionary<int, byte[]>? TryBuildVocabTokenHashMap(
        ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        string snapshotDir = context.Source.ModelDirectory;
        string tokenizerJson = System.IO.Path.Combine(snapshotDir, "tokenizer.json");
        if (!System.IO.File.Exists(tokenizerJson))
        {
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(tokenizerJson);
        }
        catch (System.IO.IOException)
        {
            return null;
        }
        if (bytes.Length == 0)
        {
            return null;
        }
        Hartonomous.Core.Text.Tokenizers.TokenizerModel model;
        try
        {
            model = Hartonomous.Core.Text.Tokenizers.HuggingFaceTokenizerParser.Parse(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        Dictionary<int, byte[]> map = new(model.Vocab.Count);
        foreach (KeyValuePair<int, Hartonomous.Core.Text.Tokenizers.VocabularyEntry> kv in model.Vocab)
        {
            ct.ThrowIfCancellationRequested();
            Hartonomous.Core.Text.TextDecomposeResult r =
                Hartonomous.Core.Text.SubstrateTextDecomposer.EmitStatic(
                    session.Batch,
                    kv.Value.TokenBytes,
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: context.ProvenanceCode,
                        TopEntityType: "word_form",
                        TrustMu: ModelDerivedTrustMu));
            map[kv.Key] = r.RootHash;
        }
        return map;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[token-attention-edge {ModelId}] no input embedding tensor; pass skipped")]
        public static partial void NoEmbedding(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-attention-edge {ModelId}] no tokenizer vocab map; pass skipped")]
        public static partial void NoTokenizerMap(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-attention-edge {ModelId}] complete — layers={Layers} edges={Edges}")]
        public static partial void PassComplete(ILogger logger, string modelId, long layers, long edges);
    }
}
