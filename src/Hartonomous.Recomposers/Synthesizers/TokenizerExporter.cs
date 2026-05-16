using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Emit a HuggingFace-compatible <c>tokenizer.json</c> backed by the
/// substrate's selected vocab. V1 writes a minimal WordLevel-style tokenizer
/// keyed on each token's content-hash placeholder text so that
/// transformers can load the model end-to-end; V2 (follow-up) will resolve
/// each VocabToken's actual surface form via substrate.recompose_text() and
/// emit a real BPE/Unigram tokenizer.
/// </summary>
public static class TokenizerExporter
{
    public static async Task WriteAsync(
        IReadOnlyList<VocabToken> vocab,
        string outputDir,
        CancellationToken ct)
    {
        using MemoryStream ms = new();
        JsonWriterOptions opts = new() { Indented = true };
        using (Utf8JsonWriter w = new(ms, opts))
        {
            w.WriteStartObject();
            w.WriteString("version", "1.0");
            w.WriteNumber("truncation", default(int?) ?? 512);
            w.WriteNull("padding");

            // Reserved special tokens at the bottom of the vocab.
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
            // Reserved indices for special tokens.
            w.WriteNumber("[PAD]", 0);
            w.WriteNumber("[UNK]", 1);
            w.WriteNumber("[CLS]", 2);
            w.WriteNumber("[SEP]", 3);
            w.WriteNumber("[MASK]", 4);
            // Substrate-selected vocab starts at index 5 (offset by special tokens).
            const int specialTokenOffset = 5;
            for (int i = 0; i < vocab.Count; i++)
            {
                w.WriteNumber(vocab[i].TokenText, i + specialTokenOffset);
            }
            w.WriteEndObject(); // vocab
            w.WriteEndObject(); // model

            w.WriteEndObject(); // root
        }

        string tokenizerPath = Path.Combine(outputDir, "tokenizer.json");
        await File.WriteAllBytesAsync(tokenizerPath, ms.ToArray(), ct).ConfigureAwait(false);

        // Also write a minimal tokenizer_config.json for transformers loaders.
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
