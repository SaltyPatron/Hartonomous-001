using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
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

    private const double ModelDerivedTrustMu = 60_000.0;
    // 5K was too aggressive — drain throughput got dominated by per-batch
    // round-trip + parameter marshalling overhead, not the actual writes.
    // 100K is well under the 7M+ size that crashed Npgsql's binary writer
    // previously (because we flush per source-row, no inner loop can
    // accumulate that much before yielding), and is large enough that
    // a 12-worker drain can keep up with embedding cosine emission rate.
    private const int FlushThreshold = 20_000;

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
        Dictionary<int, Hash32> vocabHashes = new(tokenizer.Vocab.Count);
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
                context.Compute, embed, vocabSize, hiddenDim, opts,
                onStage: msg => Log.Stage(_logger, table.Info.Name, msg));

            // Per-row firefly POINTZM + entity_model_source + entity_significance.
            for (int row = 0; row < vocabSize; row++)
            {
                ct.ThrowIfCancellationRequested();
                if (!vocabHashes.TryGetValue(row, out Hash32 wordHash)) { continue; }
                EntityHandle wordForm = new(wordHash, "word_form");
                session.Batch.AddPhysicalityPoint4d(wordForm, "firefly", x[row], y[row], z[row], magnitude[row]);
                session.Batch.AddSignificance(wordForm, "model_trust", ModelDerivedTrustMu, "positive_evidence");
                session.Batch.AddEntityModelSource(wordForm, context.Source.ModelSourceId);
                fireflies++;
                if (fireflies % FlushThreshold == 0)
                {
                    await session.MaybeFlushAsync(FlushThreshold, ct);
                }
            }
        }

        Log.Complete(_logger, context.Source.ModelId, tuplesProcessed, fireflies, edgesEmitted);
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
        catch (Exception ex) when (ex is not OperationCanceledException) // BOUNDARY: malformed tokenizer.json or transient I/O — pass yields without firefly emission.
        {
            return null;
        }
    }

    private static double SigmoidLocal(double x)
    {
        if (x > 35) { return 1.0; }
        if (x < -35) { return 0.0; }
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    private static int[] CollectUsable(bool[] usable)
    {
        int n = 0;
        for (int i = 0; i < usable.Length; i++) { if (usable[i]) { n++; } }
        int[] result = new int[n];
        int j = 0;
        for (int i = 0; i < usable.Length; i++) { if (usable[i]) { result[j++] = i; } }
        return result;
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
