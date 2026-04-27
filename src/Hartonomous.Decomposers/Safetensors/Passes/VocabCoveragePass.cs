using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Tokenizers;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Connects each model bpe_token to substrate lemma entities seeded by the
/// lexical decomposers (UD, WordNet, Wiktionary). Operates by content
/// dedup: emit a lemma entity with the canonical word-form hash; if the
/// substrate already has a matching lemma (because UD/WordNet/etc. ingested
/// it), the entity insert is a no-op and the covers_lemma edge attaches
/// to the existing entity.
///
/// Coverage methodology:
///   1. Decode each token's bytes to UTF-8 (skip raw byte-fallback tokens).
///   2. Strip the BPE/SP whitespace prefix markers (▁ U+2581, Ġ U+0120) so
///      "▁the" maps to the lemma "the".
///   3. Compute the canonical word-form hash via
///      <see cref="BaseDecomposer.ComputeWordFormHash"/> (Merkle of grapheme
///      clusters → codepoints) — same convention UD / WordNet / Wiktionary
///      use, so the hash collides naturally with their existing lemmas.
///   4. Emit a lemma entity (server-side dedup if it exists) and a
///      covers_lemma edge from the bpe_token to it.
///   5. Track per-token outcome: matched (decoded + non-special), special
///      (BOS/EOS/CLS/SEP/PAD/MASK), byte-fallback (raw bytes that aren't
///      valid UTF-8), empty.
///   6. Emit a vocab_coverage_profile entity whose hash is canonical over
///      the architecture hash + counts so identical statistics across
///      snapshots collapse to ONE profile entity.
///
/// The bpe_token entity hashes computed here MUST match those emitted by
/// <see cref="TokenizerMappingPass"/> — both use kind tag "bptk" + tokenizer
/// config hash + raw token bytes. Edges referencing a re-emitted entity
/// hash resolve to the same substrate row.
///
/// Per docs/specs/decomposers/analysis-passes.md § "VocabCoveragePass".
/// </summary>
internal sealed partial class VocabCoveragePass : IModelAnalysisPass
{
    public string PassId => "model.vocab_coverage";

    // SAME-MODEL dependency: bpe_token entities must exist before we can
    // attach covers_lemma edges to them.
    public IReadOnlyList<string> Dependencies => ["model.tokenizer_mapping"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    // U+2581 LOWER ONE EIGHTH BLOCK ("▁") — SentencePiece word-start marker.
    private static readonly byte[] SentencePieceWordStart = [0xE2, 0x96, 0x81];
    // U+0120 LATIN CAPITAL LETTER G WITH DOT ABOVE ("Ġ") — GPT-2 byte-BPE
    // marker indicating a leading space in the original text.
    private static readonly byte[] Gpt2WordStart = [0xC4, 0xA0];

    private readonly ILogger _logger;

    public VocabCoveragePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        string snapshotDir = context.Source.ModelDirectory;
        string tokenizerJson = Path.Combine(snapshotDir, "tokenizer.json");
        if (!File.Exists(tokenizerJson))
        {
            Log.NoTokenizer(_logger, context.Source.ModelId);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(tokenizerJson, ct);
        if (bytes.Length == 0)
        {
            return;
        }

        TokenizerModel model;
        try
        {
            model = HuggingFaceTokenizerParser.Parse(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // BOUNDARY: malformed tokenizer.json — pass yields without writes; TokenizerMappingPass will already have skipped the same file the same way.
        {
            Log.ParseFailed(_logger, ex, context.Source.ModelId);
            return;
        }

        long total = 0;
        long matched = 0;
        long special = 0;
        long byteFallback = 0;
        long empty = 0;

        List<VocabularyEntry> sortedVocab = new(model.Vocab.Count);
        foreach (KeyValuePair<int, VocabularyEntry> kv in model.Vocab)
        {
            sortedVocab.Add(kv.Value);
        }
        sortedVocab.Sort(static (a, b) => a.TokenId.CompareTo(b.TokenId));

        foreach (VocabularyEntry entry in sortedVocab)
        {
            ct.ThrowIfCancellationRequested();
            total++;

            if (entry.IsSpecial)
            {
                special++;
                continue;
            }
            if (entry.TokenBytes.Length == 0)
            {
                empty++;
                continue;
            }

            // Strip the BPE/SP word-start prefix before decoding.
            byte[] surface = StripWordStartPrefix(entry.TokenBytes);
            if (surface.Length == 0)
            {
                empty++;
                continue;
            }

            string? text = TryDecodeUtf8(surface);
            if (text is null || text.Length == 0)
            {
                byteFallback++;
                continue;
            }

            byte[] tokenHash = ComputeBpeTokenHash(context, entry.TokenBytes);
            byte[] lemmaHash = BaseDecomposer.ComputeWordFormHash(text);

            EntityHandle bpeTokenEntity = session.Batch.AddEntity(tokenHash, "bpe_token");
            EntityHandle lemmaEntity = session.Batch.AddEntity(lemmaHash, "lemma");

            session.Batch.AddEdge("covers_lemma", context.ProvenanceCode,
            [
                new EdgeMemberSpec(bpeTokenEntity, null, "source", 0),
                new EdgeMemberSpec(lemmaEntity, null, "target", 1),
            ]);

            matched++;
            await session.MaybeFlushAsync(FlushThreshold, ct);
        }

        // vocab_coverage_profile — content is the statistics tuple itself.
        // Two architectures with identical coverage stats collapse to ONE
        // profile entity, both linked via has_vocab_coverage edges.
        byte[] profileHash = new CanonicalSignatureBuilder(context.Compute.Common, "vcvp")
            .WriteInt64LE(total)
            .WriteInt64LE(matched)
            .WriteInt64LE(special)
            .WriteInt64LE(byteFallback)
            .WriteInt64LE(empty)
            .Finalize();

        EntityHandle profileEntity = session.Batch.AddEntity(profileHash, "vocab_coverage_profile");
        session.Batch.AddSignificance(profileEntity, "model_trust", ModelDerivedTrustMu);
        session.Batch.AddEntityModelSource(profileEntity, context.Source.ModelSourceId);

        session.Batch.AddEdge("has_vocab_coverage", context.ProvenanceCode,
        [
            new EdgeMemberSpec(session.ModelEntity, null, "source", 0),
            new EdgeMemberSpec(profileEntity, null, "target", 1),
        ]);

        Log.PassComplete(_logger, context.Source.ModelId, total, matched, special, byteFallback, empty);
    }

    /// <summary>
    /// Strips the SentencePiece (▁) and GPT-2 (Ġ) word-start prefix bytes if
    /// present at the head. These markers convey "this token starts a new
    /// word" — placement metadata, not part of the lemma's identity. Same
    /// convention every modern HuggingFace tokenizer uses.
    /// </summary>
    private static byte[] StripWordStartPrefix(byte[] tokenBytes)
    {
        if (StartsWith(tokenBytes, SentencePieceWordStart))
        {
            int len = tokenBytes.Length - SentencePieceWordStart.Length;
            byte[] tail = new byte[len];
            Array.Copy(tokenBytes, SentencePieceWordStart.Length, tail, 0, len);
            return tail;
        }
        if (StartsWith(tokenBytes, Gpt2WordStart))
        {
            int len = tokenBytes.Length - Gpt2WordStart.Length;
            byte[] tail = new byte[len];
            Array.Copy(tokenBytes, Gpt2WordStart.Length, tail, 0, len);
            return tail;
        }
        return tokenBytes;
    }

    private static bool StartsWith(byte[] haystack, byte[] needle)
    {
        if (haystack.Length < needle.Length) { return false; }
        for (int i = 0; i < needle.Length; i++)
        {
            if (haystack[i] != needle[i]) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Decodes UTF-8, returning null on invalid sequences. We use
    /// <see cref="UTF8Encoding"/> with strict throw semantics so byte-fallback
    /// tokens (raw 0x80–0xFF without a valid lead byte) classify as
    /// non-decodable rather than producing replacement characters that would
    /// pollute the lemma hash.
    /// </summary>
    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException) // BOUNDARY: tokenizer byte-fallback tokens (raw 0x80–0xFF without a valid UTF-8 lead) are valid vocab entries that simply have no UTF-8-string projection — they cannot bind to lemmas, return null and let the caller skip.
        {
            return null;
        }
    }

    /// <summary>
    /// Mirror of TokenizerMappingPass.ComputeBpeTokenHash — must match exactly
    /// so re-emitted bpe_token entities resolve to the same substrate row.
    /// Content-only: token bytes ARE the entity identity. Vocabulary membership
    /// is an in_vocabulary edge, not part of the hash.
    /// </summary>
    private static byte[] ComputeBpeTokenHash(
        ModelPassContext context, byte[] tokenBytes)
        => new CanonicalSignatureBuilder(context.Compute.Common, "bptk")
            .WriteBytes(tokenBytes)
            .Finalize();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[vocab-coverage {ModelId}] no tokenizer.json; pass skipped")]
        public static partial void NoTokenizer(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[vocab-coverage {ModelId}] tokenizer.json parse failed; pass skipped")]
        public static partial void ParseFailed(ILogger logger, Exception ex, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[vocab-coverage {ModelId}] complete — total={Total}, matched={Matched}, special={Special}, byte_fallback={ByteFallback}, empty={Empty}")]
        public static partial void PassComplete(ILogger logger, string modelId, long total, long matched, long special, long byteFallback, long empty);
    }
}
