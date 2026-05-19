using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Recomposition;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Emit a HuggingFace-compatible <c>tokenizer.json</c> backed by the
/// substrate's selected vocab with REAL surface forms.
///
/// Construction:
///   1. Resolve each vocab hash to its UTF-8 surface form via the C# bulk-tier
///      walker (<see cref="BulkTierContentWalk"/>). Replaces the removed
///      <c>substrate.recompose_text_bulk</c> SQL wrapper per Gate 1 item #36.
///   2. Reserve 5 special-token slots (PAD/UNK/CLS/SEP/MASK) at the start
///      of the vocab map.
///   3. Emit WordLevel tokenizer.json with vocab map keyed on actual
///      surface form strings → token id. This makes "Hello" tokenize to
///      the substrate's hello word_form (if it's in vocab) instead of UNK.
///
/// Collision handling:
///   Two entities with the same surface form (e.g. the codepoint U+0041
///   "A" and the word_form "A") are possible. Substrate identity is
///   content-addressed by hash, but tokenizer.json keys are surface-form
///   strings. First-write wins; subsequent collisions are dropped with a
///   counter for telemetry.
///
/// Empty-surface handling:
///   Tokens whose recompose_text returns empty (e.g. classification
///   entities like POS that aren't text-decomposable) get a synthetic
///   sentinel form: "&lt;type:hex(hash[0..8])&gt;". These don't collide
///   with user-text and serve as classification anchors the model can
///   attend to without polluting the lexical surface.
/// </summary>
public static class TokenizerExporter
{
    public static async Task WriteAsync(
        IReadOnlyList<VocabToken> vocab,
        NpgsqlDataSource dataSource,
        string outputDir,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> hashHexToSurface =
            await ResolveSurfaceFormsAsync(vocab, dataSource, ct).ConfigureAwait(false);

        Dictionary<string, int> vocabMap = new(StringComparer.Ordinal);
        const int specialTokenOffset = 5;
        long surfaceCollisions = 0;
        long emptySurfaceFallbacks = 0;

        for (int i = 0; i < vocab.Count; i++)
        {
            VocabToken t = vocab[i];
            string hashHex = Convert.ToHexString(t.EntityHash);
            string? surface = hashHexToSurface.TryGetValue(hashHex, out string? s) ? s : null;
            if (string.IsNullOrEmpty(surface))
            {
                surface = $"<wf_{hashHex[..16].ToLowerInvariant()}>";
                emptySurfaceFallbacks++;
            }

            int tokenId = i + specialTokenOffset;
            if (!vocabMap.TryAdd(surface, tokenId))
            {
                surfaceCollisions++;
            }
        }

        Console.Out.WriteLine(
            $"TokenizerExporter: vocab={vocab.Count} surfaces_resolved={hashHexToSurface.Count} "
            + $"empty_fallbacks={emptySurfaceFallbacks} surface_collisions={surfaceCollisions} "
            + $"final_vocab_keys={vocabMap.Count}");

        using MemoryStream ms = new();
        JsonWriterOptions opts = new() { Indented = true };
        using (Utf8JsonWriter w = new(ms, opts))
        {
            w.WriteStartObject();
            w.WriteString("version", "1.0");
            w.WriteNumber("truncation", default(int?) ?? 512);
            w.WriteNull("padding");

            w.WriteStartArray("added_tokens");
            WriteSpecialToken(w, 0, "[PAD]");
            WriteSpecialToken(w, 1, "[UNK]");
            WriteSpecialToken(w, 2, "[CLS]");
            WriteSpecialToken(w, 3, "[SEP]");
            WriteSpecialToken(w, 4, "[MASK]");
            w.WriteEndArray();

            w.WriteNull("normalizer");
            w.WriteStartObject("pre_tokenizer");
            w.WriteString("type", "Whitespace");
            w.WriteEndObject();

            w.WriteNull("post_processor");
            w.WriteNull("decoder");

            w.WriteStartObject("model");
            w.WriteString("type", "WordLevel");
            w.WriteString("unk_token", "[UNK]");
            w.WriteStartObject("vocab");
            w.WriteNumber("[PAD]", 0);
            w.WriteNumber("[UNK]", 1);
            w.WriteNumber("[CLS]", 2);
            w.WriteNumber("[SEP]", 3);
            w.WriteNumber("[MASK]", 4);
            foreach ((string surface, int tokenId) in vocabMap)
            {
                w.WriteNumber(surface, tokenId);
            }
            w.WriteEndObject(); // vocab
            w.WriteEndObject(); // model

            w.WriteEndObject(); // root
        }

        string tokenizerPath = Path.Combine(outputDir, "tokenizer.json");
        await File.WriteAllBytesAsync(tokenizerPath, ms.ToArray(), ct).ConfigureAwait(false);

        string tokenizerConfig = """
        {
          "model_max_length": 512,
          "padding_side": "right",
          "truncation_side": "right",
          "unk_token": "[UNK]",
          "pad_token": "[PAD]",
          "cls_token": "[CLS]",
          "sep_token": "[SEP]",
          "mask_token": "[MASK]",
          "tokenizer_class": "PreTrainedTokenizerFast",
          "hartonomous_substrate_derived": true
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(outputDir, "tokenizer_config.json"),
            tokenizerConfig, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveSurfaceFormsAsync(
        IReadOnlyList<VocabToken> vocab,
        NpgsqlDataSource dataSource,
        CancellationToken ct)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (vocab.Count == 0)
        {
            return result;
        }

        // Gate 1 item #36: per-token walk via the C# bulk-tier walker.
        // recompose_text_bulk is gone; per-token walks reuse one open
        // connection but issue tier-batched queries per token.
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < vocab.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            byte[] hashBytes = vocab[i].EntityHash;
            string hashHex = Convert.ToHexString(hashBytes);
            byte[] surfaceBytes = await BulkTierContentWalk.RecomposeAsync(
                conn, new Hash32(hashBytes), maxDepth: 32, ct).ConfigureAwait(false);
            string surface = surfaceBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(surfaceBytes);
            result[hashHex] = surface;
        }
        return result;
    }

    private static void WriteSpecialToken(Utf8JsonWriter w, int id, string content)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("content", content);
        w.WriteBoolean("special", true);
        w.WriteBoolean("single_word", false);
        w.WriteBoolean("lstrip", false);
        w.WriteBoolean("rstrip", false);
        w.WriteBoolean("normalized", false);
        w.WriteEndObject();
    }
}
