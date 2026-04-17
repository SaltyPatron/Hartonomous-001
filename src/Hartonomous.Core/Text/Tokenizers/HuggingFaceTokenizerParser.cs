using System.Text;
using System.Text.Json;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Parses HuggingFace <c>tokenizer.json</c> into a <see cref="TokenizerModel"/>.
/// Canonicalizes the JSON (lexicographic key ordering, insignificant whitespace
/// stripped, null-valued optional fields dropped, merge strings split into
/// <c>{left,right}</c> pairs) before hashing so cosmetically different configs
/// with the same semantics produce the same <c>ConfigHash</c>.
/// </summary>
public static class HuggingFaceTokenizerParser
{
    public static TokenizerModel Parse(ReadOnlySpan<byte> tokenizerJsonUtf8)
    {
        if (tokenizerJsonUtf8.IsEmpty)
        {
            throw new ArgumentException("tokenizer.json payload is empty.", nameof(tokenizerJsonUtf8));
        }

        JsonDocument doc = JsonDocument.Parse(tokenizerJsonUtf8.ToArray());
        JsonElement root = doc.RootElement;

        IReadOnlyList<Normalizer> normalizers = ParseNormalizers(root);
        IReadOnlyList<PreTokenizer> preTokenizers = ParsePreTokenizers(root);
        IReadOnlyList<PostProcessor> postProcessors = ParsePostProcessors(root);
        (TokenizerKind kind, IReadOnlyDictionary<int, VocabularyEntry> vocab, IReadOnlyList<MergeRule> merges) =
            ParseModel(root);
        SpecialTokens specials = ParseSpecials(root, vocab);

        byte[] canonical = Canonicalize(root);
        byte[] configHash = BlakeHash(canonical);

        return new TokenizerModel(
            kind,
            configHash,
            normalizers,
            preTokenizers,
            postProcessors,
            vocab,
            merges,
            specials);
    }

    private static IReadOnlyList<Normalizer> ParseNormalizers(JsonElement root)
    {
        if (!root.TryGetProperty("normalizer", out JsonElement n) || n.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<Normalizer>();
        }
        return FlattenSequence<Normalizer>(n, el => new Normalizer(
            ReadString(el, "type") ?? "Unknown",
            ReadParameters(el, "type")));
    }

    private static IReadOnlyList<PreTokenizer> ParsePreTokenizers(JsonElement root)
    {
        if (!root.TryGetProperty("pre_tokenizer", out JsonElement n) || n.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<PreTokenizer>();
        }
        return FlattenSequence<PreTokenizer>(n, el => new PreTokenizer(
            ReadString(el, "type") ?? "Unknown",
            ReadParameters(el, "type")));
    }

    private static IReadOnlyList<PostProcessor> ParsePostProcessors(JsonElement root)
    {
        if (!root.TryGetProperty("post_processor", out JsonElement n) || n.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<PostProcessor>();
        }
        return FlattenSequence<PostProcessor>(n, el => new PostProcessor(
            ReadString(el, "type") ?? "Unknown",
            ReadParameters(el, "type")));
    }

    private static List<T> FlattenSequence<T>(JsonElement el, Func<JsonElement, T> factory)
    {
        List<T> acc = new();
        string? type = ReadString(el, "type");
        if (type == "Sequence" && el.TryGetProperty("normalizers", out JsonElement seq1))
        {
            foreach (JsonElement inner in seq1.EnumerateArray())
            {
                acc.Add(factory(inner));
            }
            return acc;
        }
        if (type == "Sequence" && el.TryGetProperty("pretokenizers", out JsonElement seq2))
        {
            foreach (JsonElement inner in seq2.EnumerateArray())
            {
                acc.Add(factory(inner));
            }
            return acc;
        }
        acc.Add(factory(el));
        return acc;
    }

    private static (TokenizerKind Kind,
                    IReadOnlyDictionary<int, VocabularyEntry> Vocab,
                    IReadOnlyList<MergeRule> Merges) ParseModel(JsonElement root)
    {
        if (!root.TryGetProperty("model", out JsonElement m) || m.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("tokenizer.json is missing the required 'model' object.");
        }

        string? typeName = ReadString(m, "type");
        TokenizerKind kind = typeName switch
        {
            "BPE" => TokenizerKind.Bpe,
            "ByteLevel" or "ByteLevelBPE" => TokenizerKind.ByteBpe,
            "WordPiece" => TokenizerKind.WordPiece,
            "Unigram" or "SentencePiece" => TokenizerKind.SentencePiece,
            "CharDelimiterSplit" => TokenizerKind.CharLevel,
            _ => TokenizerKind.Unknown,
        };

        Dictionary<int, VocabularyEntry> vocab = ReadVocab(m);
        List<MergeRule> merges = ReadMerges(m);
        return (kind, vocab, merges);
    }

    private static Dictionary<int, VocabularyEntry> ReadVocab(JsonElement model)
    {
        Dictionary<int, VocabularyEntry> vocab = new();

        if (model.TryGetProperty("vocab", out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in v.EnumerateObject())
                {
                    if (p.Value.TryGetInt32(out int id))
                    {
                        vocab[id] = new VocabularyEntry(id, Encoding.UTF8.GetBytes(p.Name), false);
                    }
                }
            }
            else if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in v.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 1)
                    {
                        string token = row[0].GetString() ?? string.Empty;
                        int id = row.GetArrayLength() >= 2 ? row[1].GetInt32() : vocab.Count;
                        vocab[id] = new VocabularyEntry(id, Encoding.UTF8.GetBytes(token), false);
                    }
                }
            }
        }

        if (model.TryGetProperty("added_tokens", out JsonElement added) && added.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in added.EnumerateArray())
            {
                int id = row.GetProperty("id").GetInt32();
                string content = row.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
                bool special = row.TryGetProperty("special", out JsonElement s) && s.GetBoolean();
                vocab[id] = new VocabularyEntry(id, Encoding.UTF8.GetBytes(content), special);
            }
        }

        return vocab;
    }

    private static List<MergeRule> ReadMerges(JsonElement model)
    {
        List<MergeRule> merges = new();
        if (!model.TryGetProperty("merges", out JsonElement m) || m.ValueKind != JsonValueKind.Array)
        {
            return merges;
        }

        int priority = 0;
        foreach (JsonElement row in m.EnumerateArray())
        {
            byte[] left;
            byte[] right;
            if (row.ValueKind == JsonValueKind.String)
            {
                string s = row.GetString() ?? string.Empty;
                int sp = s.IndexOf(' ');
                if (sp < 0)
                {
                    continue;
                }
                left = Encoding.UTF8.GetBytes(s[..sp]);
                right = Encoding.UTF8.GetBytes(s[(sp + 1)..]);
            }
            else if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 2)
            {
                left = Encoding.UTF8.GetBytes(row[0].GetString() ?? string.Empty);
                right = Encoding.UTF8.GetBytes(row[1].GetString() ?? string.Empty);
            }
            else
            {
                continue;
            }
            merges.Add(new MergeRule(left, right, priority++));
        }
        return merges;
    }

    private static SpecialTokens ParseSpecials(
        JsonElement root,
        IReadOnlyDictionary<int, VocabularyEntry> vocab)
    {
        int? FindByContent(string content)
        {
            foreach (KeyValuePair<int, VocabularyEntry> kv in vocab)
            {
                if (Encoding.UTF8.GetString(kv.Value.TokenBytes) == content)
                {
                    return kv.Key;
                }
            }
            return null;
        }

        int? bos = null, eos = null, pad = null, unk = null, mask = null;
        List<int> additional = new();

        if (root.TryGetProperty("post_processor", out JsonElement pp) && pp.ValueKind == JsonValueKind.Object)
        {
            if (pp.TryGetProperty("special_tokens", out JsonElement st) && st.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in st.EnumerateObject())
                {
                    string kind = entry.Name.ToLowerInvariant();
                    string content = ReadString(entry.Value, "id") ?? entry.Name;
                    int? id = FindByContent(content);
                    switch (kind)
                    {
                        case "bos" or "[bos]" or "<s>": bos = id; break;
                        case "eos" or "[eos]" or "</s>": eos = id; break;
                    }
                }
            }
        }

        if (root.TryGetProperty("model", out JsonElement model))
        {
            if (model.TryGetProperty("unk_token", out JsonElement unkTok))
            {
                unk = FindByContent(unkTok.GetString() ?? string.Empty);
            }
        }

        if (root.TryGetProperty("added_tokens", out JsonElement added) && added.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in added.EnumerateArray())
            {
                if (!row.TryGetProperty("special", out JsonElement s) || !s.GetBoolean())
                {
                    continue;
                }
                int id = row.GetProperty("id").GetInt32();
                string content = row.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
                switch (content.ToLowerInvariant())
                {
                    case "<bos>" or "[bos]" or "<s>": bos ??= id; break;
                    case "<eos>" or "[eos]" or "</s>": eos ??= id; break;
                    case "<pad>" or "[pad]": pad ??= id; break;
                    case "<unk>" or "[unk]": unk ??= id; break;
                    case "<mask>" or "[mask]": mask ??= id; break;
                    default: additional.Add(id); break;
                }
            }
        }

        return new SpecialTokens(bos, eos, pad, unk, mask, additional);
    }

    private static Dictionary<string, string> ReadParameters(JsonElement el, string excludeKey)
    {
        Dictionary<string, string> p = new();
        if (el.ValueKind != JsonValueKind.Object)
        {
            return p;
        }
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (prop.Name == excludeKey)
            {
                continue;
            }
            p[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => prop.Value.GetRawText(),
            };
        }
        return p;
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(w, root);
        }
        return ms.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter w, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in el.EnumerateObject()
                    .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (p.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }
                    w.WritePropertyName(p.Name);
                    WriteCanonical(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    WriteCanonical(w, item);
                }
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(el.GetString());
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(el.GetRawText());
                break;
            case JsonValueKind.True:
                w.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                w.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                w.WriteNullValue();
                break;
        }
    }

    private static byte[] BlakeHash(ReadOnlySpan<byte> bytes)
    {
        return Blake3.Hash(bytes);
    }
}
