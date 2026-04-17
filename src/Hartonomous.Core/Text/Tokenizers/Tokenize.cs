using System.Buffers;
using System.Text;

namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Re-tokenization primitive. Walks the parsed <see cref="TokenizerModel"/>
/// directly — no HuggingFace / SentencePiece runtime bridge. Encode emits
/// tokens with byte-exact offsets into the ORIGINAL input (offsets are
/// preserved through pre-tokenizer splitting); BPE / WordPiece / Tiktoken
/// models all route through the same greedy longest-match path because the
/// substrate only needs "what token does this byte range resolve to" — the
/// merge-ordering re-derivation required for arithmetic-exact HF parity is a
/// separate operation used only during recompose-time student-model training.
/// </summary>
public static class Tokenize
{
    /// <summary>
    /// Encode <paramref name="inputUtf8"/> into a flat token sequence with
    /// offsets back into the original bytes.
    /// </summary>
    public static IReadOnlyList<TokenWithOffset> Encode(TokenizerModel tokenizer, ReadOnlySpan<byte> inputUtf8)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        if (inputUtf8.IsEmpty)
        {
            return Array.Empty<TokenWithOffset>();
        }

        Dictionary<string, int> byContent = BuildLookup(tokenizer);
        int unkId = tokenizer.Specials.Unk ?? -1;
        List<TokenWithOffset> output = new();

        int idx = 0;
        while (idx < inputUtf8.Length)
        {
            int matchedLen = LongestMatch(inputUtf8, idx, byContent, out int tokenId);
            if (matchedLen == 0)
            {
                if (unkId >= 0)
                {
                    output.Add(new TokenWithOffset(unkId, idx, 1));
                }
                idx++;
            }
            else
            {
                output.Add(new TokenWithOffset(tokenId, idx, matchedLen));
                idx += matchedLen;
            }
        }

        return output;
    }

    /// <summary>
    /// Decode a token id stream back into a byte buffer. Unknown ids become
    /// empty spans — decode is a best-effort inverse; the substrate preserves
    /// the original content via the source entity regardless.
    /// </summary>
    public static byte[] Decode(TokenizerModel tokenizer, ReadOnlySpan<int> tokenIds)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        if (tokenIds.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        ArrayBufferWriter<byte> writer = new();
        foreach (int id in tokenIds)
        {
            if (tokenizer.Vocab.TryGetValue(id, out VocabularyEntry? entry))
            {
                writer.Write(entry.TokenBytes);
            }
        }
        return writer.WrittenSpan.ToArray();
    }

    private static Dictionary<string, int> BuildLookup(TokenizerModel tokenizer)
    {
        Dictionary<string, int> dict = new(tokenizer.Vocab.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<int, VocabularyEntry> kv in tokenizer.Vocab)
        {
            string key = Encoding.UTF8.GetString(kv.Value.TokenBytes);
            if (!dict.ContainsKey(key))
            {
                dict[key] = kv.Key;
            }
        }
        return dict;
    }

    private static int LongestMatch(
        ReadOnlySpan<byte> input,
        int start,
        Dictionary<string, int> byContent,
        out int tokenId)
    {
        tokenId = -1;
        int bestLen = 0;
        int end = Math.Min(input.Length, start + 64);
        for (int len = end - start; len > 0; len--)
        {
            string candidate = Encoding.UTF8.GetString(input.Slice(start, len));
            if (byContent.TryGetValue(candidate, out int id))
            {
                tokenId = id;
                bestLen = len;
                break;
            }
        }
        return bestLen;
    }
}
