using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Decomposition;

public static class EntityContentHashResolver
{
    public static IReadOnlyList<byte[]> GetCandidateHashes(
        string content,
        IReadOnlyList<string> entityTypeCodes)
    {
        HashSet<byte[]> hashes = new(ByteArrayEqualityComparer.Instance);

        AddSurfaceHashes(content, entityTypeCodes, hashes);

        string lowered = content.ToLowerInvariant();
        if (!string.Equals(lowered, content, StringComparison.Ordinal))
        {
            AddSurfaceHashes(lowered, entityTypeCodes, hashes);
        }

        return [.. hashes];
    }

    private static void AddSurfaceHashes(
        string content,
        IReadOnlyList<string> entityTypeCodes,
        HashSet<byte[]> hashes)
    {
        hashes.Add(Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan()));

        if (RequiresStructuredTextHash(entityTypeCodes))
        {
            hashes.Add(ComputeWordFormHash(content));
        }

        if (RequiresCodepointHash(entityTypeCodes) && IsSingleRune(content, out int codepoint))
        {
            hashes.Add(HashCodepoint(codepoint));
        }

    }

    private static bool RequiresStructuredTextHash(IReadOnlyList<string> entityTypeCodes)
    {
        for (int i = 0; i < entityTypeCodes.Count; i++)
        {
            if (entityTypeCodes[i] is "grapheme_cluster" or "word_form" or "lemma" or "text_composition" or "document" or "paragraph" or "language_name")
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequiresCodepointHash(IReadOnlyList<string> entityTypeCodes)
    {
        for (int i = 0; i < entityTypeCodes.Count; i++)
        {
            if (entityTypeCodes[i] == "codepoint")
            {
                return true;
            }
        }

        return false;
    }

    // bpe_token-specific hash routing removed — BPE tokens are now word_form
    // entities, hashed via the canonical structured-text path along with
    // every other UTF-8 surface form.

    private static bool IsSingleRune(string content, out int codepoint)
    {
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(content);
        if (!elements.MoveNext())
        {
            codepoint = 0;
            return false;
        }

        string element = elements.GetTextElement();
        if (elements.MoveNext())
        {
            codepoint = 0;
            return false;
        }

        StringRuneEnumerator runes = element.EnumerateRunes();
        if (!runes.MoveNext())
        {
            codepoint = 0;
            return false;
        }

        Rune rune = runes.Current;
        if (runes.MoveNext())
        {
            codepoint = 0;
            return false;
        }

        codepoint = rune.Value;
        return true;
    }

    private static byte[] ComputeWordFormHash(string form)
    {
        List<byte[]> gcHashes = [];
        TextElementEnumerator textElements = StringInfo.GetTextElementEnumerator(form);
        while (textElements.MoveNext())
        {
            string graphemeCluster = textElements.GetTextElement();
            List<byte[]> codepointHashes = [];
            foreach (Rune rune in graphemeCluster.EnumerateRunes())
            {
                codepointHashes.Add(HashCodepoint(rune.Value));
            }

            gcHashes.Add(ComputeMerkleHash(codepointHashes));
        }

        return ComputeMerkleHash(gcHashes);
    }

    private static byte[] HashCodepoint(int cpValue)
    {
        Span<byte> cpBytes = stackalloc byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return Blake3.Hash(cpBytes);
    }

    private static byte[] ComputeMerkleHash(List<byte[]> childHashes)
    {
        byte[] concat = new byte[childHashes.Count * Blake3.HashLen];
        for (int i = 0; i < childHashes.Count; i++)
        {
            childHashes[i].CopyTo(concat.AsSpan(i * Blake3.HashLen));
        }

        return Merkle.Hash(concat.AsSpan());
    }
}
