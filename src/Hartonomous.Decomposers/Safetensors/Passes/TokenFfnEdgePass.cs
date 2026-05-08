using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Direct token↔token edge emission from FFN tensors via projection through
/// the model's input embedding matrix. Companion to <see cref="TokenCrossEdgePass"/>:
/// where that pass extracts token-pair evidence from the embedding matrix
/// itself, this pass extracts token-pair evidence from the FFN
/// transformations — what FFN neurons consider co-active.
///
/// For each FFN_DOWN row r (one residual-stream output direction):
///   1. Project r against the embedding matrix: <c>resp_T = embed[T] · r</c>
///      gives "how strongly does this FFN direction align with token T's
///      embedding."
///   2. Take top-K tokens by |resp_T| as the row's "co-activated set."
///   3. For each pair (T_i, T_j) within that set, emit edge
///      <c>model_ffn_factor(T_i, T_j)</c> with attestation_type
///      <c>model_ffn_full_path</c>. The clique semantics is "this FFN
///      neuron treats these tokens as related" — repeated across many
///      neurons / models, the substrate's accumulated rating identifies
///      consensus token-pair clusters.
///
/// Depends on <see cref="TokenCrossEdgePass"/> (and transitively
/// <see cref="TokenizerMappingPass"/>) so the token (word_form) entities
/// the edges reference exist when this pass emits.
///
/// Sparsity discipline (Law #11): per-row adaptive noise floor on |resp_T|;
/// pairs whose response is below the floor don't get into the top-K. K = 32
/// per neuron — balances volume (vocab × layers × ffn_dim × K² edges) with
/// signal preservation. Symmetric edge identity sorts participants by hash.
/// </summary>
internal sealed partial class TokenFfnEdgePass : IModelAnalysisPass
{
    public string PassId => "model.token_ffn_edges";
    public IReadOnlyList<string> Dependencies => ["model.token_cross_edges"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerRow = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public TokenFfnEdgePass(ILogger logger)
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

        // Load embedding once; share across all FFN tensors.
        double[] embed = SafetensorsReader.ReadTensorAsDouble(e.Info);

        Dictionary<int, byte[]>? vocabHashes = TryBuildVocabTokenHashMap(context, session, ct);
        if (vocabHashes is null || vocabHashes.Count == 0)
        {
            Log.NoTokenizerMap(_logger, context.Source.ModelId);
            return;
        }

        long ffnRowsProcessed = 0;
        long edgesEmitted = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Classification.Role != TensorRole.FfnDown)
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                continue;
            }
            int rows = (int)t.Info.Shape[0];
            int cols = (int)t.Info.Shape[1];
            // FFN_DOWN: rows = residual_dim (= hidden_dim), cols = ffn_intermediate_dim.
            // Each ROW is one residual-stream output direction; we project
            // that direction against the embedding (which lives in
            // hidden_dim space). Embedding shape is [vocab, hidden_dim] so
            // hidden_dim must match this tensor's row count.
            if (rows != hiddenDim)
            {
                continue;
            }

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);

            // Iterate ffn-intermediate columns (ffn_dim). Each column is one
            // neuron's contribution to the residual; gather it as a hidden_dim
            // vector by reading column c across all rows.
            for (int colIdx = 0; colIdx < cols; colIdx++)
            {
                ct.ThrowIfCancellationRequested();

                // neuron_vec[h] = flat[h * cols + colIdx] for h in 0..rows.
                // Project against embedding to get response per token.
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
                if (noiseFloor <= 0)
                {
                    continue;
                }

                // Top-K tokens by |response|, above noise floor.
                (int Token, double Resp, double Abs)[] top = new (int, double, double)[TopKPerRow];
                int filled = 0;
                double minAbs = double.PositiveInfinity;
                int minIdx = -1;
                for (int v = 0; v < vocabSize; v++)
                {
                    if (!vocabHashes.ContainsKey(v))
                    {
                        continue;
                    }
                    double abs = Math.Abs(response[v]);
                    if (abs < noiseFloor)
                    {
                        continue;
                    }
                    if (filled < TopKPerRow)
                    {
                        top[filled] = (v, response[v], abs);
                        filled++;
                        if (filled == TopKPerRow)
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
                if (filled < 2)
                {
                    continue;
                }

                // Emit edges among the top-K clique. Symmetric edge: sort
                // participants by hash. Edge type: model_ffn_factor.
                // attestation_type: model_ffn_full_path.
                double absSum = 0;
                for (int k = 0; k < filled; k++) { absSum += top[k].Abs; }
                double absMean = absSum / filled;

                for (int i = 0; i < filled; i++)
                {
                    for (int j = i + 1; j < filled; j++)
                    {
                        byte[] hashA = vocabHashes[top[i].Token];
                        byte[] hashB = vocabHashes[top[j].Token];
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
                        double mu = Math.Clamp(1500.0 + ((pairAbs / absMean) * 200.0), 500.0, 2500.0);

                        EdgeSignificanceSpec[] sigSpecs =
                        [
                            new EdgeSignificanceSpec("model_trust", "model_ffn_full_path", mu),
                        ];

                        session.Batch.AddEdge(
                            "model_ffn_factor",
                            context.ProvenanceCode,
                            [
                                new EdgeMemberSpec(aH, "source", 0),
                                new EdgeMemberSpec(bH, "target", 1),
                            ],
                            sigSpecs);
                        edgesEmitted++;
                        if (edgesEmitted % FlushThreshold == 0)
                        {
                            await session.MaybeFlushAsync(FlushThreshold, ct);
                        }
                    }
                }
                ffnRowsProcessed++;
            }
        }

        Log.PassComplete(_logger, context.Source.ModelId, ffnRowsProcessed, edgesEmitted);
    }

    private static void RecomputeMin(
        (int Token, double Resp, double Abs)[] buf, int filled,
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
            if (c != 0)
            {
                return c;
            }
        }
        return a.Length.CompareTo(b.Length);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[token-ffn-edge {ModelId}] no input embedding tensor; pass skipped")]
        public static partial void NoEmbedding(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-ffn-edge {ModelId}] no tokenizer vocab map; pass skipped")]
        public static partial void NoTokenizerMap(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-ffn-edge {ModelId}] complete — ffn_rows={Rows} edges={Edges}")]
        public static partial void PassComplete(ILogger logger, string modelId, long rows, long edges);
    }
}
