using System.Text;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Parses tiktoken <c>.tiktoken</c> files: one row per token of the form
/// <c>{base64(token_bytes)} {rank}</c>. Rank is the merge priority and is
/// assigned as the <see cref="VocabularyEntry.TokenId"/>. The substrate
/// reconstructs merges from neighbouring ranks — tiktoken files don't carry an
/// explicit merge table because byte-BPE merges are implicit in the rank order.
/// </summary>
public static class TiktokenTokenizerParser
{
    public static TokenizerModel Parse(ReadOnlySpan<byte> tiktokenFileUtf8)
    {
        if (tiktokenFileUtf8.IsEmpty)
        {
            throw new ArgumentException("tiktoken payload is empty.", nameof(tiktokenFileUtf8));
        }

        Dictionary<int, VocabularyEntry> vocab = new();
        string text = Encoding.UTF8.GetString(tiktokenFileUtf8);
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.EndsWith('\r') ? line[..^1] : line;
            if (trimmed.Length == 0)
            {
                continue;
            }
            int sp = trimmed.IndexOf(' ');
            if (sp < 0)
            {
                continue;
            }
            string b64 = trimmed[..sp];
            string rankStr = trimmed[(sp + 1)..];
            if (!int.TryParse(rankStr, out int rank))
            {
                continue;
            }
            byte[] tokenBytes;
            try
            {
                tokenBytes = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                continue;
            }
            vocab[rank] = new VocabularyEntry(rank, tokenBytes, false);
        }

        byte[] configHash = Blake3.Hash(tiktokenFileUtf8);

        return new TokenizerModel(
            TokenizerKind.Tiktoken,
            configHash,
            new List<Normalizer>(),
            new List<PreTokenizer> { new("ByteLevel", new Dictionary<string, string>()) },
            new List<PostProcessor>(),
            vocab,
            Array.Empty<MergeRule>(),
            new SpecialTokens(null, null, null, null, null, Array.Empty<int>()));
    }
}
