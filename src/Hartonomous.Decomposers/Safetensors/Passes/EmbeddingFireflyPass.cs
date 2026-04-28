using System.Buffers.Binary;
using System.IO;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Track 1 — embedding wholesale ingestion. For each Track-1 token-embedding
/// tensor:
///
///   1. Decode tensor bytes to f64 row-major.
///   2. Compute per-row L2 magnitude (the M coordinate of the firefly).
///   3. Project rows to the first 3 non-trivial eigenvectors of the normalized
///      Laplacian of the symmetric k-NN cosine graph (X, Y, Z).
///   4. For each row i: look up the symbolic <c>bpe_token</c> entity hashed
///      by the tokenizer's vocab_index→token_bytes mapping. The token "king"
///      appears in exactly one row per model's embedding matrix; across N
///      ingested models, N fireflies all attach to the SAME shared bpe_token
///      entity (hashed by token bytes only). This is the substrate-level
///      foundation for Voronoi consensus: cross-model agreement on a token's
///      4D position is computable because all of model A's fireflies and all
///      of model B's fireflies for "king" hang off ONE entity.
///   5. Attach <c>embedding_firefly</c> physicality (POINTZM WKB) to the
///      bpe_token entity, tagged with the model's provenance. One physicality
///      per (bpe_token × model) pair.
///   6. Edge <c>has_token_id</c> from the embedding tensor to the bpe_token
///      records the (model, vocab_index) placement on the edge member position.
///
/// Per docs/specs/decomposers/analysis-passes.md § "EmbeddingFireflyPass" and
/// docs/specs/engine/embedding-physicality.md § "Firefly entity":
///   "A firefly is NOT its own entity row. It is a physicality of an existing
///    entity. For token embeddings: the entity is the corresponding bpe_token
///    entity. Its firefly physicality records 'this is where model X thinks
///    this token sits in 4D.'"
/// </summary>
internal sealed partial class EmbeddingFireflyPass : IModelAnalysisPass
{
    public string PassId => "model.embedding_fireflies";

    public IReadOnlyList<string> Dependencies => [];

    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FireflyBatchSize = 50_000;
    private const int MaxFireflyRows = 50_000;

    private readonly ILogger _logger;
    private readonly LaplacianEigenmapOptions _baseOptions;

    public EmbeddingFireflyPass(ILogger logger, LaplacianEigenmapOptions? options = null)
    {
        _logger = logger;
        _baseOptions = options ?? LaplacianEigenmapOptions.Default;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ulong baseSeed = context.DeriveSeed(PassId);

        // Load the tokenizer ONCE per model so we can map vocab_index → token_bytes
        // and look up the SAME shared bpe_token entity (hashed by token bytes only)
        // that TokenizerMappingPass creates / will create. Whichever pass runs first
        // wins on insert; the UNIQUE constraint on (hash, entity_type_id) makes the
        // other pass's AddEntity dedupe to the existing row. Without the tokenizer
        // (no tokenizer.json present), we cannot map rows to tokens — the firefly
        // anchoring is impossible and we skip with a warning rather than fabricate
        // ghost entities.
        TokenizerModel? tokenizer = TryLoadTokenizer(context.Source.ModelDirectory);
        if (tokenizer is null)
        {
            Log.NoTokenizer(_logger, context.Source.ModelId, context.Source.ModelDirectory);
            return;
        }

        IReadOnlyDictionary<int, VocabularyEntry> vocabByIndex = tokenizer.Vocab;

        int tensorOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            if (!t.Classification.Role.IsTrack1())
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                Log.SkipNon2D(_logger, t.Info.Name, t.Info.Shape.Length);
                continue;
            }
            long rowsLong = t.Info.Shape[0];
            if (rowsLong < 4 || rowsLong > MaxFireflyRows)
            {
                Log.SkipOutOfRange(_logger, t.Info.Name, rowsLong);
                continue;
            }

            int rows = (int)rowsLong;
            int cols = (int)t.Info.Shape[1];

            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, rows, cols);
            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double[] magnitude = ComputeRowMagnitudes(flat, rows, cols);

            // Per-tensor seed = baseSeed XOR low 64 bits of tensor content hash.
            // Same tensor content + same model + same pass → same seed.
            ulong tensorSeed = baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0);
            int seed = (int)(tensorSeed & 0x7FFFFFFF);
            LaplacianEigenmapOptions opts = _baseOptions with { Seed = seed };

            (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(
                flat, rows, cols, opts,
                onStage: msg => Log.Stage(_logger, t.Info.Name, msg));

            int anchored = 0;
            int unanchored = 0;
            for (int i = 0; i < rows; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Look up the token at vocab_index i. If the tokenizer doesn't have
                // an entry for this row (e.g., embedding tensor is wider than vocab,
                // or this is a position embedding rather than a token embedding),
                // skip — no ghost entity. Real fix for non-token-embedding Track-1
                // tensors (position embeddings, codebooks, object queries) is to
                // anchor them to position_index / codebook_entry / object_query_slot
                // entities per docs/specs/engine/embedding-physicality.md § "Firefly
                // entity"; that's a follow-up for the role-specific anchoring.
                if (!vocabByIndex.TryGetValue(i, out VocabularyEntry? entry))
                {
                    unanchored++;
                    continue;
                }

                // Hash by token bytes ONLY — same canonical signature
                // TokenizerMappingPass uses. "king" from Llama and "king" from Qwen
                // produce the same hash → same bpe_token entity → both fireflies
                // attach to the SAME row, and Voronoi consensus aggregates over the
                // shared entity's firefly cloud.
                byte[] tokenHash = new CanonicalSignatureBuilder(context.Compute.Common, "bptk")
                    .WriteBytes(entry.TokenBytes)
                    .Finalize();

                EntityHandle bpeToken = session.Batch.AddEntity(tokenHash, "bpe_token");

                // Attach this model's firefly as a physicality of the shared entity.
                // Provenance on the entity_model_source link distinguishes which
                // model contributed which firefly.
                session.Batch.AddPhysicalityPoint4d(bpeToken, "embedding_firefly", x[i], y[i], z[i], magnitude[i]);
                session.Batch.AddSignificance(bpeToken, "model_trust", ModelDerivedTrustMu);
                session.Batch.AddEntityModelSource(bpeToken, context.Source.ModelSourceId);

                // Edge from this model's embedding tensor to the shared bpe_token.
                // The vocab_index is recoverable via the tokenizer.json that
                // TokenizerMappingPass already parsed (has_token_in_tokenizer edge
                // chain), so we don't need to encode it on the edge member position
                // (which is short and would overflow at vocab > 32767 — Llama and
                // Qwen vocabs both exceed that). Position=1 is the conventional
                // target ordinal.
                session.Batch.AddEdge("has_token_id", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(t.Entity, "source", 0),
                    new EdgeMemberSpec(bpeToken, "target", 1),
                ]);

                anchored++;
                await session.MaybeFlushAsync(FireflyBatchSize, ct);
            }
            Log.TensorComplete(_logger, t.Info.Name, anchored, unanchored);
        }
    }

    private static TokenizerModel? TryLoadTokenizer(string snapshotDir)
    {
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson))
        {
            return null;
        }
        try
        {
            byte[] bytes = File.ReadAllBytes(tokenizerJson);
            if (bytes.Length == 0) { return null; }
            return HuggingFaceTokenizerParser.Parse(bytes);
        }
        catch // BOUNDARY: a malformed tokenizer.json must not halt the model — pass yields without firefly emission.
        {
            return null;
        }
    }

    private static double[] ComputeRowMagnitudes(double[] flat, int rows, int cols)
    {
        double[] mag = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            long off = (long)i * cols;
            double sumSq = 0;
            for (int j = 0; j < cols; j++)
            {
                double v = flat[off + j];
                sumSq += v * v;
            }
            mag[i] = Math.Sqrt(sumSq);
        }
        return mag;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly {Idx}] {Name} starting (rows={Rows}, cols={Cols})")]
        public static partial void TensorStart(ILogger logger, int idx, string name, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly] {Name} complete ({Anchored} fireflies anchored to shared bpe_tokens, {Unanchored} rows skipped — vocab miss or non-token-embedding)")]
        public static partial void TensorComplete(ILogger logger, string name, int anchored, int unanchored);

        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly] {Name}: {Stage}")]
        public static partial void Stage(ILogger logger, string name, string stage);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[firefly] {Name} not 2-D (rank={Rank}); skipped")]
        public static partial void SkipNon2D(ILogger logger, string name, int rank);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[firefly] {Name} rows={Rows} out of supported range; skipped")]
        public static partial void SkipOutOfRange(ILogger logger, string name, long rows);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[firefly {ModelId}] no tokenizer.json in {SnapshotDir}; firefly emission skipped — fireflies require vocab_index→token_bytes mapping to anchor to shared bpe_token entities")]
        public static partial void NoTokenizer(ILogger logger, string modelId, string snapshotDir);
    }
}
