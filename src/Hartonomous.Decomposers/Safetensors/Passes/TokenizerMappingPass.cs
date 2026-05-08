using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Parses each model's shipped tokenizer artifact (tokenizer.json /
/// vocab.json / sentencepiece / tiktoken) and lifts its vocabulary into the
/// substrate as content-addressed entities. Without this pass the model
/// knows "token id 7234 is at vocab row 7234" but the substrate has no
/// symbolic anchor onto which seed-lexicon evidence (WordNet, Wiktionary,
/// UD) can later be corroborated. With this pass:
///
///   - One <c>tokenizer_model</c> entity per parsed config (hash =
///     canonicalized config bytes from <see cref="HuggingFaceTokenizerParser"/>).
///     Identical tokenizer.json across snapshots collapses to ONE substrate
///     entity with N <c>has_tokenizer_model</c> edges.
///   - One <c>bpe_token</c> entity per <see cref="VocabularyEntry"/>, hashed
///     by canonical (tokenizer_config_hash, token_bytes). Identical tokens
///     across tokenizers that share a config (e.g., Llama-tokenizer family)
///     dedupe naturally.
///   - <c>has_token_in_tokenizer</c> edge tokenizer_model → bpe_token per
///     vocabulary row.
///   - <c>in_vocabulary</c> edge bpe_token → model_architecture (existing
///     edge type from migration 0019).
///   - For every multi-codepoint token: substrate.sequence rows linking
///     bpe_token → codepoint at each ordinal position. Codepoint entities
///     dedup with the UCD seed via <see cref="BaseDecomposer.HashCodepoint"/>.
///
/// Per docs/specs/decomposers/tokenizers.md § "Entity mapping" and
/// migration 0045_tokenizer_mapping.up.sql.
///
/// The existing <see cref="EmbeddingFireflyPass"/> currently emits bpe_token
/// entities hashed by 4D firefly coordinates. Per the embedding-physicality
/// spec a firefly is properly a physicality of an existing bpe_token, not
/// its own entity row — that realignment is the plan's A10 task and will
/// retire the geometric-identity bpe_token rows in favor of these symbolic
/// ones. Until then the two coexist in the bpe_token partition under
/// different hashes; they do not collide.
/// </summary>
internal sealed partial class TokenizerMappingPass : IModelAnalysisPass
{
    public string PassId => "model.tokenizer_mapping";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public TokenizerMappingPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        string snapshotDir = context.Source.ModelDirectory;
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson))
        {
            // Other tokenizer formats (sentencepiece .model, tiktoken,
            // wordpiece vocab.txt) are spec'd parsers but are routed in
            // follow-up work; the present pass handles the dominant
            // HuggingFace tokenizer.json case.
            Log.NoTokenizerJson(_logger, context.Source.ModelId, snapshotDir);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(tokenizerJson, ct);
        if (bytes.Length == 0)
        {
            Log.EmptyTokenizerJson(_logger, context.Source.ModelId);
            return;
        }

        TokenizerModel model;
        try
        {
            model = HuggingFaceTokenizerParser.Parse(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // BOUNDARY: malformed tokenizer.json must not halt the model — the pass records nothing and yields to the next pass.
        {
            Log.ParseFailed(_logger, ex, context.Source.ModelId);
            return;
        }

        Log.TokenizerParsed(_logger, context.Source.ModelId, model.Kind,
            model.Vocab.Count, model.Merges.Count);

        // tokenizer_model entity — content-addressed by the parser's
        // ConfigHash (already a BLAKE3 over canonical bytes).
        EntityHandle tokenizerEntity = session.Batch.AddEntity(model.ConfigHash, "tokenizer_model");
        session.Batch.AddSignificance(tokenizerEntity, "model_trust", ModelDerivedTrustMu);
        session.Batch.AddEntityModelSource(tokenizerEntity, context.Source.ModelSourceId);

        session.Batch.AddEdge("has_tokenizer_model", context.ProvenanceCode,
        [
            new EdgeMemberSpec(session.ModelEntity, "source", 0),
            new EdgeMemberSpec(tokenizerEntity, "target", 1),
        ]);

        long tokenCount = 0;
        long sequenceRows = 0;

        // Stable iteration order: ascending by token id. Ensures the substrate
        // sees tokens in the same order across runs/machines for Law #6.
        List<VocabularyEntry> sortedVocab = new(model.Vocab.Count);
        foreach (KeyValuePair<int, VocabularyEntry> kv in model.Vocab)
        {
            sortedVocab.Add(kv.Value);
        }
        sortedVocab.Sort(static (a, b) => a.TokenId.CompareTo(b.TokenId));

        foreach (VocabularyEntry entry in sortedVocab)
        {
            ct.ThrowIfCancellationRequested();

            // Token bytes route through the canonical text decomposer, same
            // path corpora use. "King" from Llama's vocab and "King" from
            // Moby Dick produce the SAME word_form entity hash because they
            // are decomposed identically (codepoint → grapheme_cluster →
            // word_form Merkle composition). Cross-model AND cross-source
            // dedup falls out for free. The pass no longer hashes tokens
            // under a "bptk" kind tag — that broke corpus⇄model collapse.
            //
            // Codepoint sequence rows are emitted internally by the native
            // text_decompose; we no longer enumerate UTF-8 codepoints here.
            Hartonomous.Core.Text.TextDecomposeResult tokenResult =
                Hartonomous.Core.Text.SubstrateTextDecomposer.EmitStatic(
                    session.Batch,
                    entry.TokenBytes,
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: context.ProvenanceCode,
                        TopEntityType: "word_form",
                        TrustMu: ModelDerivedTrustMu));
            EntityHandle tokenEntity = tokenResult.RootHandle;
            session.Batch.AddEntityModelSource(tokenEntity, context.Source.ModelSourceId);
            session.Batch.AddSignificance(
                tokenEntity, "model_trust", ModelDerivedTrustMu, "model_embedding_proximity");

            session.Batch.AddEdge("has_token_in_tokenizer", context.ProvenanceCode,
            [
                new EdgeMemberSpec(tokenizerEntity, "source", 0),
                new EdgeMemberSpec(tokenEntity, "target", 1),
            ]);
            session.Batch.AddEdge("in_vocabulary", context.ProvenanceCode,
            [
                new EdgeMemberSpec(tokenEntity, "source", 0),
                new EdgeMemberSpec(session.ModelEntity, "target", 1),
            ]);

            tokenCount++;
            await session.MaybeFlushAsync(FlushThreshold, ct);
        }

        Log.PassComplete(_logger, context.Source.ModelId, tokenCount, sequenceRows);
    }

    /// <summary>
    /// Canonical bpe_token signature: kind tag "bptk" + raw TokenBytes only.
    /// The token's CONTENT is its bytes. Vocabulary membership (which model
    /// uses this token) is an in_vocabulary edge, not part of identity.
    /// Token "the" from Llama and Qwen → SAME entity → in_vocabulary edges
    /// to both architectures → cross-model evidence accumulates.
    /// </summary>
    private static byte[] ComputeBpeTokenHash(
        ModelPassContext context, byte[] tokenBytes)
        => new CanonicalSignatureBuilder(context.Compute.Common, "bptk")
            .WriteBytes(tokenBytes)
            .Finalize();

    /// <summary>
    /// Streaming UTF-8 codepoint enumerator. Skips invalid sequences so a
    /// tokenizer's byte-fallback rows (which can contain raw 0x80–0xFF bytes
    /// that aren't valid standalone UTF-8) don't halt the pass — those tokens
    /// still get an entity, they just have no codepoint composition.
    /// </summary>
    private static IEnumerable<int> EnumerateUtf8Codepoints(byte[] utf8)
    {
        int i = 0;
        while (i < utf8.Length)
        {
            byte b0 = utf8[i];
            int cp;
            int len;

            if (b0 < 0x80)
            {
                cp = b0;
                len = 1;
            }
            else if ((b0 & 0xE0) == 0xC0 && i + 1 < utf8.Length)
            {
                cp = ((b0 & 0x1F) << 6) | (utf8[i + 1] & 0x3F);
                len = 2;
            }
            else if ((b0 & 0xF0) == 0xE0 && i + 2 < utf8.Length)
            {
                cp = ((b0 & 0x0F) << 12) | ((utf8[i + 1] & 0x3F) << 6) | (utf8[i + 2] & 0x3F);
                len = 3;
            }
            else if ((b0 & 0xF8) == 0xF0 && i + 3 < utf8.Length)
            {
                cp = ((b0 & 0x07) << 18) | ((utf8[i + 1] & 0x3F) << 12)
                   | ((utf8[i + 2] & 0x3F) << 6) | (utf8[i + 3] & 0x3F);
                len = 4;
            }
            else
            {
                // Invalid lead byte (e.g., raw byte-fallback). Skip one byte
                // and continue — the bpe_token entity exists either way; the
                // substrate just has no codepoint composition for this slot.
                i++;
                continue;
            }

            yield return cp;
            i += len;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[tokenizer-mapping {ModelId}] no tokenizer.json in {SnapshotDir}; pass skipped")]
        public static partial void NoTokenizerJson(ILogger logger, string modelId, string snapshotDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[tokenizer-mapping {ModelId}] tokenizer.json is empty; pass skipped")]
        public static partial void EmptyTokenizerJson(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[tokenizer-mapping {ModelId}] tokenizer.json parse failed; pass skipped")]
        public static partial void ParseFailed(ILogger logger, Exception ex, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[tokenizer-mapping {ModelId}] parsed {Kind} tokenizer ({VocabSize} tokens, {MergeCount} merges)")]
        public static partial void TokenizerParsed(ILogger logger, string modelId, TokenizerKind kind, int vocabSize, int mergeCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "[tokenizer-mapping {ModelId}] pass complete — {Tokens} bpe_token entities, {SequenceRows} composed_of_codepoints sequence rows")]
        public static partial void PassComplete(ILogger logger, string modelId, long tokens, long sequenceRows);
    }
}
