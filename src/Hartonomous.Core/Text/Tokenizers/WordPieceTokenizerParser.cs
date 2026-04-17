using System.Text;
using System.Text.Json;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Parses a classic WordPiece tokenizer: <c>vocab.txt</c> (one token per line,
/// token id = 0-based line index) plus the companion <c>tokenizer_config.json</c>
/// that names the normalizer / special tokens.  <see cref="TokenizerModel.ConfigHash"/>
/// is BLAKE3 over the raw vocab bytes concatenated with the raw config bytes —
/// both artifacts are content; both contribute to identity.
/// </summary>
public static class WordPieceTokenizerParser
{
    public static TokenizerModel Parse(
        ReadOnlySpan<byte> vocabTxtUtf8,
        ReadOnlySpan<byte> tokenizerConfigJsonUtf8)
    {
        if (vocabTxtUtf8.IsEmpty)
        {
            throw new ArgumentException("vocab.txt payload is empty.", nameof(vocabTxtUtf8));
        }

        Dictionary<int, VocabularyEntry> vocab = ReadVocab(vocabTxtUtf8);
        (SpecialTokens specials, IReadOnlyList<Normalizer> normalizers) = ReadConfig(tokenizerConfigJsonUtf8, vocab);

        byte[] configHash = Hash(vocabTxtUtf8, tokenizerConfigJsonUtf8);

        return new TokenizerModel(
            TokenizerKind.WordPiece,
            configHash,
            normalizers,
            new List<PreTokenizer> { new("WhitespacePunctuation", new Dictionary<string, string>()) },
            Array.Empty<PostProcessor>(),
            vocab,
            Array.Empty<MergeRule>(),
            specials);
    }

    private static Dictionary<int, VocabularyEntry> ReadVocab(ReadOnlySpan<byte> utf8)
    {
        Dictionary<int, VocabularyEntry> vocab = new();
        string text = Encoding.UTF8.GetString(utf8);
        int id = 0;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.EndsWith('\r') ? line[..^1] : line;
            vocab[id] = new VocabularyEntry(id, Encoding.UTF8.GetBytes(trimmed), IsSpecialToken(trimmed));
            id++;
        }
        return vocab;
    }

    private static bool IsSpecialToken(string token)
    {
        return token.Length >= 2 && token[0] == '[' && token[^1] == ']';
    }

    private static (SpecialTokens, IReadOnlyList<Normalizer>) ReadConfig(
        ReadOnlySpan<byte> configUtf8,
        IReadOnlyDictionary<int, VocabularyEntry> vocab)
    {
        if (configUtf8.IsEmpty)
        {
            return (new SpecialTokens(null, null, null, null, null, Array.Empty<int>()),
                    Array.Empty<Normalizer>());
        }

        using JsonDocument doc = JsonDocument.Parse(configUtf8.ToArray());
        JsonElement root = doc.RootElement;

        int? Find(string? content)
        {
            if (content is null)
            {
                return null;
            }
            foreach (KeyValuePair<int, VocabularyEntry> kv in vocab)
            {
                if (Encoding.UTF8.GetString(kv.Value.TokenBytes) == content)
                {
                    return kv.Key;
                }
            }
            return null;
        }

        string? bosTok = ReadStringProp(root, "bos_token");
        string? eosTok = ReadStringProp(root, "eos_token");
        string? padTok = ReadStringProp(root, "pad_token");
        string? unkTok = ReadStringProp(root, "unk_token");
        string? maskTok = ReadStringProp(root, "mask_token");
        string? clsTok = ReadStringProp(root, "cls_token");
        string? sepTok = ReadStringProp(root, "sep_token");

        int? bos = Find(bosTok ?? clsTok);
        int? eos = Find(eosTok ?? sepTok);
        int? pad = Find(padTok);
        int? unk = Find(unkTok);
        int? mask = Find(maskTok);

        List<Normalizer> normalizers = new();
        bool doLower = root.TryGetProperty("do_lower_case", out JsonElement dlc) && dlc.ValueKind == JsonValueKind.True;
        if (doLower)
        {
            normalizers.Add(new Normalizer("Lowercase", new Dictionary<string, string>()));
        }
        bool stripAccents = root.TryGetProperty("strip_accents", out JsonElement sa) && sa.ValueKind == JsonValueKind.True;
        if (stripAccents)
        {
            normalizers.Add(new Normalizer("StripAccents", new Dictionary<string, string>()));
        }

        return (new SpecialTokens(bos, eos, pad, unk, mask, Array.Empty<int>()), normalizers);
    }

    private static string? ReadStringProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement p))
        {
            return null;
        }
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Object when p.TryGetProperty("content", out JsonElement c) => c.GetString(),
            _ => null,
        };
    }

    private static byte[] Hash(ReadOnlySpan<byte> vocab, ReadOnlySpan<byte> config)
    {
        byte[] buf = new byte[vocab.Length + config.Length];
        vocab.CopyTo(buf);
        config.CopyTo(buf.AsSpan(vocab.Length));
        return Blake3.Hash(buf);
    }
}
