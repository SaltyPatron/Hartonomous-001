using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §II.13 + §IV. Foundational TuplePass:
/// processes EmbeddingLookup tuples (token embedding tables) and produces
/// the substrate's word_form bridge entities + per-(model, token) firefly
/// POINTZM physicalities + per-token-pair model_concept_similarity
/// attestations on model_concept_similarity edges.
///
/// Other TuplePasses (AttentionBlock, Ffn, etc.) emit attestations on edges
/// between word_form entities — those word_form entities must exist before
/// the attestation edges can be created. This pass produces them by routing
/// every vocab token through SubstrateTextDecomposer.EmitStatic. Same content
/// across models collapses to one word_form entity (BLAKE3 of token bytes).
///
/// Math:
///   1. Load tokenizer.json from the model snapshot directory; build vocab_index → token_bytes map.
///   2. For each vocab row: route token_bytes through SubstrateTextDecomposer.EmitStatic;
///      record vocab_index → word_form_root_hash.
///   3. Read the embedding table tensor as f64 [vocab, hidden_dim].
///   4. Compute per-row L2 magnitude (firefly M coordinate).
///   5. Project rows to 3 non-trivial Laplacian eigenvectors of cosine k-NN graph
///      (firefly X, Y, Z coordinates) via Compute.Ingestion.LaplacianEigenmap.
///   6. For each row i: attach POINTZM (X[i], Y[i], Z[i], M[i]) physicality on
///      the row's word_form entity via session.Batch.AddPhysicalityPoint4d;
///      fire entity_significance with attestation_type=model_embedding_proximity;
///      record entity_model_source linkage.
///   7. For each row pair (T, S) where |cos(embed[T], embed[S])| above adaptive
///      noise floor: emit edge model_concept_similarity(T, S) with
///      attestation_type=model_input_embedding. Top-K-per-token clip for volume.
/// </summary>
internal sealed partial class EmbeddingLookupTuplePass : IModelAnalysisPass
{
    public string PassId => "tuple.embedding_lookup";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int TopKPerToken = 64;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;
    private readonly LaplacianEigenmapOptions _baseOptions;

    public EmbeddingLookupTuplePass(ILogger logger, LaplacianEigenmapOptions? options = null)
    {
        _logger = logger;
        _baseOptions = options ?? LaplacianEigenmapOptions.Default;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        TokenizerModel? tokenizer = TryLoadTokenizer(context.Source.ModelDirectory);
        if (tokenizer is null)
        {
            Log.NoTokenizer(_logger, context.Source.ModelId, context.Source.ModelDirectory);
            return;
        }

        // Resolve vocab → word_form-hash via canonical text decomposition.
        // SubstrateTextDecomposer.EmitStatic is idempotent and content-addressed —
        // running it produces the same hash regardless of how many models have
        // already attested the same token's content.
        Dictionary<int, byte[]> vocabHashes = new(tokenizer.Vocab.Count);
        foreach (KeyValuePair<int, VocabularyEntry> kv in tokenizer.Vocab)
        {
            ct.ThrowIfCancellationRequested();
            TextDecomposeResult r = SubstrateTextDecomposer.EmitStatic(
                session.Batch, kv.Value.TokenBytes,
                new TextDecomposeOptions(
                    ProvenanceCode: context.ProvenanceCode,
                    TopEntityType: "word_form",
                    TrustMu: ModelDerivedTrustMu));
            vocabHashes[kv.Key] = r.RootHash;
        }
        await session.MaybeFlushAsync(FlushThreshold, ct);

        ulong baseSeed = context.DeriveSeed(PassId);

        long tuplesProcessed = 0;
        long fireflies = 0;
        long edgesEmitted = 0;

        foreach (ResolvedTuple t in context.ResolvedTuples)
        {
            if (t.Tuple != ArchetypeTuple.EmbeddingLookup) { continue; }
            // Only token-embedding tables anchor to word_form entities. Position
            // / type / codebook tables anchor to other content-entity types
            // and are handled by their own bridge logic.
            if (t.Modality != ModalityHint.Text) { continue; }

            TupleMember? tableMember = null;
            foreach (TupleMember m in t.Members)
            {
                if (m.Slot == TupleSlot.Table) { tableMember = m; break; }
            }
            if (tableMember is null) { continue; }
            TensorHandle table = tableMember.Tensor;
            if (table.Info.Shape.Length != 2) { continue; }

            int vocabSize = (int)table.Info.Shape[0];
            int hiddenDim = (int)table.Info.Shape[1];
            if (vocabSize < 2 || hiddenDim < 1) { continue; }

            ct.ThrowIfCancellationRequested();
            double[] embed = SafetensorsReader.ReadTensorAsDouble(table.Info);
            tuplesProcessed++;

            // Firefly emission via Laplacian eigenmap.
            double[] magnitude = new double[vocabSize];
            for (int i = 0; i < vocabSize; i++)
            {
                long off = (long)i * hiddenDim;
                double sumSq = 0;
                for (int j = 0; j < hiddenDim; j++)
                {
                    double v = embed[off + j];
                    sumSq += v * v;
                }
                magnitude[i] = Math.Sqrt(sumSq);
            }

            ulong tensorSeed = baseSeed ^ BitConverter.ToUInt64(table.ContentHash, 0);
            int seed = (int)(tensorSeed & 0x7FFFFFFF);
            LaplacianEigenmapOptions opts = _baseOptions with { Seed = seed };
            (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(
                embed, vocabSize, hiddenDim, opts,
                onStage: msg => Log.Stage(_logger, table.Info.Name, msg));

            // Per-row firefly POINTZM + entity_model_source + entity_significance.
            for (int row = 0; row < vocabSize; row++)
            {
                ct.ThrowIfCancellationRequested();
                if (!vocabHashes.TryGetValue(row, out byte[]? wordHash) || wordHash is null) { continue; }
                EntityHandle wordForm = new(wordHash, "word_form");
                session.Batch.AddPhysicalityPoint4d(wordForm, "embedding_firefly", x[row], y[row], z[row], magnitude[row]);
                session.Batch.AddSignificance(wordForm, "model_trust", ModelDerivedTrustMu, "model_embedding_proximity");
                session.Batch.AddEntityModelSource(wordForm, context.Source.ModelSourceId);
                fireflies++;
                if (fireflies % FlushThreshold == 0)
                {
                    await session.MaybeFlushAsync(FlushThreshold, ct);
                }
            }

            // Token-pair attestation edges: cosine of embedding rows.
            // Top-K-per-token + per-tensor adaptive noise floor (LTH per spec §V).
            double[] norms = new double[vocabSize];
            for (int row = 0; row < vocabSize; row++)
            {
                long off = (long)row * hiddenDim;
                double sumSq = 0;
                for (int d = 0; d < hiddenDim; d++) { double v = embed[off + d]; sumSq += v * v; }
                norms[row] = Math.Sqrt(sumSq);
            }
            bool[] usable = new bool[vocabSize];
            for (int row = 0; row < vocabSize; row++)
            {
                usable[row] = norms[row] > 1e-12 && vocabHashes.ContainsKey(row);
            }

            double noiseFloor = ComputeAdaptiveCosineNoiseFloor(embed, norms, usable, vocabSize, hiddenDim);

            (int Row, double AbsCos)[] topBuf = new (int, double)[TopKPerToken];

            for (int rowT = 0; rowT < vocabSize; rowT++)
            {
                ct.ThrowIfCancellationRequested();
                if (!usable[rowT]) { continue; }
                if (!vocabHashes.TryGetValue(rowT, out byte[]? hashT) || hashT is null) { continue; }

                int filled = 0;
                double minAbs = double.PositiveInfinity;
                int minIdx = -1;
                long offT = (long)rowT * hiddenDim;
                double normT = norms[rowT];

                for (int rowS = 0; rowS < vocabSize; rowS++)
                {
                    if (rowS == rowT || !usable[rowS]) { continue; }
                    long offS = (long)rowS * hiddenDim;
                    double dot = 0;
                    for (int d = 0; d < hiddenDim; d++)
                    {
                        dot += embed[offT + d] * embed[offS + d];
                    }
                    double absCos = Math.Abs(dot / (normT * norms[rowS]));
                    if (absCos < noiseFloor) { continue; }
                    if (filled < TopKPerToken)
                    {
                        topBuf[filled] = (rowS, absCos);
                        filled++;
                        if (filled == TopKPerToken)
                        {
                            RecomputeMin(topBuf, filled, out minAbs, out minIdx);
                        }
                    }
                    else if (absCos > minAbs)
                    {
                        topBuf[minIdx] = (rowS, absCos);
                        RecomputeMin(topBuf, filled, out minAbs, out minIdx);
                    }
                }

                for (int k = 0; k < filled; k++)
                {
                    (int otherRow, double absCos) = topBuf[k];
                    if (!vocabHashes.TryGetValue(otherRow, out byte[]? hashS) || hashS is null) { continue; }

                    EntityHandle a, b;
                    if (CompareBytes(hashT, hashS) <= 0)
                    {
                        a = new EntityHandle(hashT, "word_form");
                        b = new EntityHandle(hashS, "word_form");
                    }
                    else
                    {
                        a = new EntityHandle(hashS, "word_form");
                        b = new EntityHandle(hashT, "word_form");
                    }

                    double mu = Math.Clamp(1500.0 + (absCos * 1000.0), 500.0, 2500.0);
                    EdgeSignificanceSpec[] sig =
                    [
                        new EdgeSignificanceSpec("model_trust", "model_input_embedding", mu),
                        new EdgeSignificanceSpec("semantic_relevance", "model_input_embedding", mu),
                    ];
                    session.Batch.AddEdge("model_concept_similarity", context.ProvenanceCode,
                    [
                        new EdgeMemberSpec(a, "source", 0),
                        new EdgeMemberSpec(b, "target", 1),
                    ], sig);
                    edgesEmitted++;
                    if (edgesEmitted % FlushThreshold == 0)
                    {
                        await session.MaybeFlushAsync(FlushThreshold, ct);
                    }
                }
            }
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, fireflies, edgesEmitted);
    }

    private static double ComputeAdaptiveCosineNoiseFloor(
        double[] embed, double[] norms, bool[] usable, int vocabSize, int hiddenDim)
    {
        int sampleEnd = Math.Min(1024, vocabSize);
        if (sampleEnd < 2) { return 0.0; }
        int firstUsable = -1;
        for (int i = 0; i < vocabSize && firstUsable < 0; i++)
        {
            if (usable[i]) { firstUsable = i; }
        }
        if (firstUsable < 0) { return 0.0; }
        long offT = (long)firstUsable * hiddenDim;
        double normT = norms[firstUsable];
        double sumAbs = 0.0;
        int counted = 0;
        for (int rowS = 0; rowS < sampleEnd; rowS++)
        {
            if (rowS == firstUsable || !usable[rowS]) { continue; }
            long offS = (long)rowS * hiddenDim;
            double dot = 0.0;
            for (int d = 0; d < hiddenDim; d++) { dot += embed[offT + d] * embed[offS + d]; }
            double cos = dot / (normT * norms[rowS]);
            sumAbs += Math.Abs(cos);
            counted++;
        }
        if (counted == 0) { return 0.0; }
        return (sumAbs / counted) * NoiseFraction;
    }

    private static void RecomputeMin((int Row, double AbsCos)[] buf, int filled, out double minVal, out int minIdx)
    {
        minVal = double.PositiveInfinity;
        minIdx = -1;
        for (int i = 0; i < filled; i++)
        {
            if (buf[i].AbsCos < minVal)
            {
                minVal = buf[i].AbsCos;
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

    private static TokenizerModel? TryLoadTokenizer(string snapshotDir)
    {
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson)) { return null; }
        try
        {
            byte[] bytes = File.ReadAllBytes(tokenizerJson);
            if (bytes.Length == 0) { return null; }
            return HuggingFaceTokenizerParser.Parse(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-lookup {ModelId}] complete — tuples={Tuples} fireflies={Fireflies} edges={Edges}")]
        public static partial void Complete(ILogger logger, string modelId, long tuples, long fireflies, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-lookup {Name}] {Stage}")]
        public static partial void Stage(ILogger logger, string name, string stage);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[embedding-lookup {ModelId}] no tokenizer.json in {Dir}; embedding-lookup tuples skipped")]
        public static partial void NoTokenizer(ILogger logger, string modelId, string dir);
    }
}
