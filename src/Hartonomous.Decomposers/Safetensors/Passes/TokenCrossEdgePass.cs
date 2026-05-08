using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Direct token↔token edge emission from the model's input embedding matrix.
/// Replaces the phantom <c>embedding_position</c> per-row entity pattern
/// (Track 2 framing) with the actual architecture: every weight-encoded
/// token-pair relationship becomes an explicit edge between the EXISTING
/// token (word_form) entities, stamped with attestation_type capturing
/// which kind of model evidence produced it.
///
/// This pass: for each pair (T, S) of tokens whose embedding rows have
/// cosine similarity above the noise floor, emit edge
/// <c>model_concept_similarity(T, S)</c> with
/// <c>attestation_type = model_input_embedding</c>. Llama4-Maverick's
/// "King" and Qwen3-480B's "King" are the SAME word_form entity (post-
/// canonical-decomposition fix in TokenizerMappingPass) so the edge between
/// "King" and "Queen" accumulates attestations from every model that has
/// nonzero cosine for that pair. Cross-model consensus = repeated
/// agreement; cross-model divergence = wide sigma.
///
/// Depends on:
///   - <see cref="TokenizerMappingPass"/> — token (word_form) entities exist
///     in substrate, hashed by canonical text decomposition (not "bptk").
///   - <see cref="EmbeddingFireflyPass"/> — fireflies attached to the same
///     token entities (so this pass and that pass agree on which entity
///     each vocab row maps to).
///
/// Sparsity discipline (Substrate Law #11 — gradient jitter is not content):
/// per-tensor adaptive noise floor on the cosine value, computed as
/// <c>noiseFraction · mean(|cos|)</c> over the upper triangle. Pairs whose
/// |cos| is below the floor are not emitted.
///
/// Volume discipline: vocab² is large (32k² = 1B; 128k² = 16B). Emitting
/// every pair above noise floor is still impractical for full vocabs. The
/// pass uses a top-K-per-token rule: for each row T, emit edges only to
/// the K tokens with the highest |cos(embed[T], embed[S])|. Default K = 64.
/// At vocab=32k that is 32k × 64 = ~2M edges per model — bounded and
/// concentrated on the model's strongest learned associations.
///
/// Determinism (Law #6): exact pairwise cosine via single-precision dense
/// matmul (embed @ embed^T) accumulated in f64. Same model bytes → same
/// matrix → same top-K-per-row → byte-identical edge set across runs.
/// </summary>
internal sealed partial class TokenCrossEdgePass : IModelAnalysisPass
{
    public string PassId => "model.token_cross_edges";
    public IReadOnlyList<string> Dependencies => ["model.tokenizer_mapping"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerToken = 64;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public TokenCrossEdgePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        // Find the input embedding tensor. Tensor classifier tags it via
        // TensorRole.TokenEmbedding. Skip the pass if no embedding is
        // identifiable — emitting edges against the wrong tensor would
        // corrupt the substrate's accumulated knowledge.
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
            Log.NoEmbeddingTensor(_logger, context.Source.ModelId);
            return;
        }

        TensorHandle e = embeddingTensor;
        int vocabSize = (int)e.Info.Shape[0];
        int hiddenDim = (int)e.Info.Shape[1];
        if (vocabSize < 2 || hiddenDim < 1)
        {
            Log.EmbeddingShapeUnusable(_logger, context.Source.ModelId, vocabSize, hiddenDim);
            return;
        }

        // Resolve the vocab → token-entity-hash map by replaying the
        // tokenizer parse and routing every token through the canonical
        // text decomposer (same path TokenizerMappingPass uses post-bptk
        // fix). EmitStatic populates session.Batch on first call and
        // short-circuits via the text cache on subsequent calls, so the
        // hashes here line up bit-for-bit with the entities
        // TokenizerMappingPass already emitted.
        Dictionary<int, byte[]>? vocabHashes = TryBuildVocabTokenHashMap(context, session, ct);
        if (vocabHashes is null || vocabHashes.Count == 0)
        {
            Log.NoTokenizerMap(_logger, context.Source.ModelId);
            return;
        }

        // Load the embedding as f64.
        double[] embed = SafetensorsReader.ReadTensorAsDouble(e.Info);

        // Row-normalize so the matmul yields cosine directly.
        double[] norms = new double[vocabSize];
        for (int row = 0; row < vocabSize; row++)
        {
            long off = (long)row * hiddenDim;
            double sumSq = 0.0;
            for (int d = 0; d < hiddenDim; d++)
            {
                double v = embed[off + d];
                sumSq += v * v;
            }
            norms[row] = Math.Sqrt(sumSq);
        }

        // Skip rows with zero norm (placeholder/unused vocab slots) to avoid
        // division-by-zero and meaningless edges.
        bool[] rowUsable = new bool[vocabSize];
        for (int row = 0; row < vocabSize; row++)
        {
            rowUsable[row] = norms[row] > 1e-12 && vocabHashes.ContainsKey(row);
        }

        // Top-K-per-token by |cos|. For each row T, scan all rows S != T,
        // compute cos(T, S), keep the K largest by absolute value.
        long edgesEmitted = 0;
        long pairsScanned = 0;
        long pairsBelowNoise = 0;

        // Precompute the global noise floor over a sample of pairs to get
        // mean(|cos|). Full O(vocab²) sample is too large; we sample the
        // first row against all others as a proxy. Adaptive per-tensor;
        // matches the pattern PerRowContentPass uses (Substrate Law #11).
        double noiseFloor = ComputeAdaptiveCosineNoiseFloor(embed, norms, rowUsable, vocabSize, hiddenDim);

        // Reusable buffer for top-K extraction per row.
        (int OtherRow, double Cos, double AbsCos)[] topBuf = new (int, double, double)[TopKPerToken];

        for (int rowT = 0; rowT < vocabSize; rowT++)
        {
            ct.ThrowIfCancellationRequested();
            if (!rowUsable[rowT])
            {
                continue;
            }
            if (!vocabHashes.TryGetValue(rowT, out byte[]? tokenHashT) || tokenHashT is null)
            {
                continue;
            }

            int filled = 0;
            double minAbsInTop = double.PositiveInfinity;
            int minIdxInTop = -1;
            long offT = (long)rowT * hiddenDim;
            double normT = norms[rowT];

            for (int rowS = 0; rowS < vocabSize; rowS++)
            {
                if (rowS == rowT || !rowUsable[rowS])
                {
                    continue;
                }
                long offS = (long)rowS * hiddenDim;
                double dot = 0.0;
                for (int d = 0; d < hiddenDim; d++)
                {
                    dot += embed[offT + d] * embed[offS + d];
                }
                double cos = dot / (normT * norms[rowS]);
                double absCos = Math.Abs(cos);
                pairsScanned++;
                if (absCos < noiseFloor)
                {
                    pairsBelowNoise++;
                    continue;
                }

                if (filled < TopKPerToken)
                {
                    topBuf[filled] = (rowS, cos, absCos);
                    filled++;
                    if (filled == TopKPerToken)
                    {
                        RecomputeMin(topBuf, filled, out minAbsInTop, out minIdxInTop);
                    }
                }
                else if (absCos > minAbsInTop)
                {
                    topBuf[minIdxInTop] = (rowS, cos, absCos);
                    RecomputeMin(topBuf, filled, out minAbsInTop, out minIdxInTop);
                }
            }

            // Emit edges. Symmetric edge: sort participants by hash so
            // (T, S) and (S, T) collapse to one row. mu derived from cos
            // value clipped to [500, 2500].
            for (int k = 0; k < filled; k++)
            {
                (int otherRow, double cos, _) = topBuf[k];
                if (!vocabHashes.TryGetValue(otherRow, out byte[]? tokenHashS) || tokenHashS is null)
                {
                    continue;
                }

                EntityHandle aHandle;
                EntityHandle bHandle;
                if (CompareBytes(tokenHashT, tokenHashS) <= 0)
                {
                    aHandle = new EntityHandle(tokenHashT, "word_form");
                    bHandle = new EntityHandle(tokenHashS, "word_form");
                }
                else
                {
                    aHandle = new EntityHandle(tokenHashS, "word_form");
                    bHandle = new EntityHandle(tokenHashT, "word_form");
                }

                // mu band: |cos|=1 → 2500; cos near zero → 1500. Sign of cos
                // affects nothing for now (anti-correlation is also evidence
                // that the model relates these tokens, just with opposite
                // residual contribution).
                double mu = Math.Clamp(1500.0 + (Math.Abs(cos) * 1000.0), 500.0, 2500.0);

                EdgeSignificanceSpec[] sigSpecs =
                [
                    new EdgeSignificanceSpec("model_trust", "model_input_embedding", mu),
                    new EdgeSignificanceSpec("semantic_relevance", "model_input_embedding", mu),
                ];

                session.Batch.AddEdge(
                    "model_concept_similarity",
                    context.ProvenanceCode,
                    [
                        new EdgeMemberSpec(aHandle, "source", 0),
                        new EdgeMemberSpec(bHandle, "target", 1),
                    ],
                    sigSpecs);

                edgesEmitted++;
                if (edgesEmitted % FlushThreshold == 0)
                {
                    await session.MaybeFlushAsync(FlushThreshold, ct);
                }
            }
        }

        Log.PassComplete(_logger, context.Source.ModelId, vocabSize, edgesEmitted, pairsScanned, pairsBelowNoise, noiseFloor);
    }

    private static double ComputeAdaptiveCosineNoiseFloor(
        double[] embed, double[] norms, bool[] rowUsable, int vocabSize, int hiddenDim)
    {
        // Single-row sample: row 0 against the next min(1024, vocabSize-1)
        // rows. Mean of |cos| × NoiseFraction is the floor. Bounded O(hidden_dim
        // × 1024).
        int sampleEnd = Math.Min(1024, vocabSize);
        if (sampleEnd < 2)
        {
            return 0.0;
        }
        // Find first usable row.
        int firstUsable = -1;
        for (int i = 0; i < vocabSize && firstUsable < 0; i++)
        {
            if (rowUsable[i]) { firstUsable = i; }
        }
        if (firstUsable < 0)
        {
            return 0.0;
        }
        long offT = (long)firstUsable * hiddenDim;
        double normT = norms[firstUsable];
        double sumAbs = 0.0;
        int counted = 0;
        for (int rowS = 0; rowS < sampleEnd; rowS++)
        {
            if (rowS == firstUsable || !rowUsable[rowS])
            {
                continue;
            }
            long offS = (long)rowS * hiddenDim;
            double dot = 0.0;
            for (int d = 0; d < hiddenDim; d++)
            {
                dot += embed[offT + d] * embed[offS + d];
            }
            double cos = dot / (normT * norms[rowS]);
            sumAbs += Math.Abs(cos);
            counted++;
        }
        if (counted == 0)
        {
            return 0.0;
        }
        double meanAbs = sumAbs / counted;
        return meanAbs * NoiseFraction;
    }

    private static void RecomputeMin(
        (int OtherRow, double Cos, double AbsCos)[] buf, int filled,
        out double minAbs, out int minIdx)
    {
        minAbs = double.PositiveInfinity;
        minIdx = -1;
        for (int i = 0; i < filled; i++)
        {
            if (buf[i].AbsCos < minAbs)
            {
                minAbs = buf[i].AbsCos;
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

    /// <summary>
    /// Replay the tokenizer parse and produce vocab_index → token entity hash
    /// map. The hash is the canonical text-decomposition root hash for the
    /// token's UTF-8 bytes — same hash TokenizerMappingPass and
    /// EmbeddingFireflyPass produce after the bptk-prefix fix. Routes each
    /// token through SubstrateTextDecomposer.EmitStatic so the hashes line
    /// up bit-for-bit with the entities those passes already emitted; the
    /// per-decomposer text cache short-circuits the second-and-subsequent
    /// emissions of the same surface.
    /// Returns null if tokenizer.json is absent or unparseable.
    /// </summary>
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[token-cross-edge {ModelId}] no input embedding tensor identified; pass skipped")]
        public static partial void NoEmbeddingTensor(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-cross-edge {ModelId}] embedding shape unusable: vocab={Vocab} hidden={Hidden}; pass skipped")]
        public static partial void EmbeddingShapeUnusable(ILogger logger, string modelId, int vocab, int hidden);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-cross-edge {ModelId}] no tokenizer vocab map; pass skipped")]
        public static partial void NoTokenizerMap(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[token-cross-edge {ModelId}] complete — vocab={Vocab} edges={Edges} scanned={Scanned} below_noise={BelowNoise} floor={NoiseFloor:F6}")]
        public static partial void PassComplete(ILogger logger, string modelId, int vocab, long edges, long scanned, long belowNoise, double noiseFloor);
    }
}
