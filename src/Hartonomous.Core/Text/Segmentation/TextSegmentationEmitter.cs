using System;
using System.Collections.Generic;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// Single authoritative path for decomposing a sentence-level text body into the
/// substrate's canonical Merkle DAG: codepoint → grapheme_cluster → word_form
/// (UAX #29) + raw_span (between words) → text_composition.
/// <para>
/// Used by the runtime <c>TextDecomposer</c> AND by every seed decomposer that
/// emits user-visible text (Tatoeba sentences, WordNet glosses/examples,
/// Wiktionary etymologies/glosses, etc.). Routes ALL text through one path so
/// cascading deduplication works at every level — same word in any source
/// collapses to the same word_form entity, same codepoint in any source
/// collapses to the same codepoint entity.
/// </para>
/// <para>
/// Per architecture (rules/00-hartonomous-core.md, rules/10-text-and-semantics.md):
/// "Seed-uses-core is non-negotiable." Replaces the prior pattern of
/// <c>EmitWordFormMerkle(text, "text_composition")</c> which flat-merkled grapheme
/// clusters and skipped the word_form layer.
/// </para>
/// </summary>
public static class TextSegmentationEmitter
{
    /// <summary>
    /// Emit a text body as the full Merkle DAG and return the
    /// <c>text_composition</c> (or supplied entity type) handle and content hash.
    /// </summary>
    /// <param name="batch">Ingestion batch to append entities/sequences to.</param>
    /// <param name="text">UTF-8 source text.</param>
    /// <param name="properties">UAX #29 codepoint properties source (for grapheme/word boundary segmentation).</param>
    /// <param name="entityType">Top-level entity type code. Defaults to <c>text_composition</c>.</param>
    /// <param name="trustMu">Per-entity significance trust prior in the <c>source_authority</c> arena.</param>
    /// <returns>Handle and hash of the top-level entity. Empty input returns the
    /// hash of an empty Merkle root and a freshly added entity.</returns>
    public static (EntityHandle Handle, byte[] Hash) EmitTextComposition(
        IIngestionBatch batch,
        string text,
        ICodepointProperties properties,
        string entityType = "text_composition",
        double trustMu = 1000.0)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(properties);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
        ReadOnlySpan<byte> utf8 = utf8Bytes.AsSpan();

        // ── Stage 1: codepoints ─────────────────────────────────────────
        // Each codepoint hashes deterministically to the same hash regardless
        // of source (UCD seed, Tatoeba sentence, Wiktionary etymology, etc.).
        List<(int Codepoint, int ByteOffset, int ByteLength)> codepoints =
            DecodeCodepoints(utf8);

        Dictionary<int, EntityHandle> cpHandlesByValue = new(codepoints.Count);
        Dictionary<long, EntityHandle> cpHandleByOffset = new(codepoints.Count);
        Dictionary<long, byte[]> cpHashByOffset = new(codepoints.Count);
        Dictionary<long, int> codepointIndexByOffset = new(codepoints.Count);

        for (int i = 0; i < codepoints.Count; i++)
        {
            (int cp, int byteOffset, _) = codepoints[i];
            codepointIndexByOffset[byteOffset] = i;

            byte[] hash = HashCodepoint(cp);
            cpHashByOffset[byteOffset] = hash;

            if (cpHandlesByValue.TryGetValue(cp, out EntityHandle existing))
            {
                cpHandleByOffset[byteOffset] = existing;
                continue;
            }

            EntityHandle handle = batch.AddEntity(hash, "codepoint");
            cpHandlesByValue[cp] = handle;
            cpHandleByOffset[byteOffset] = handle;
        }

        // ── Stage 2: grapheme clusters (UAX #29) ────────────────────────
        // Single-codepoint clusters reuse the codepoint entity directly.
        // Multi-codepoint clusters get their own entity with a Merkle hash
        // over their constituent codepoint hashes (per architecture.md
        // "atom = leaf, composition = Merkle(child hashes)").
        // Use the .NET StringInfo-backed enumerator to guarantee UCD-conformant
        // grapheme boundaries for all scripts. The hand-rolled
        // GraphemeClusters.Enumerate currently fails 425/766 UCD test cases.
        List<GraphemeRange> graphemes = GraphemeClusters.EnumerateUsingNet(utf8);
        Dictionary<long, EntityHandle> gcHandleByOffset = new(graphemes.Count);
        Dictionary<long, byte[]> gcHashByOffset = new(graphemes.Count);
        Dictionary<byte[], EntityHandle> gcHandlesByHash = new(ByteArrayEqualityComparer.Instance);

        foreach (GraphemeRange gc in graphemes)
        {
            if (gc.CodepointLength == 1)
            {
                gcHandleByOffset[gc.ByteOffset] = cpHandleByOffset[gc.ByteOffset];
                gcHashByOffset[gc.ByteOffset] = cpHashByOffset[gc.ByteOffset];
                continue;
            }

            byte[] gcHash = ComputeGraphemeClusterHash(
                gc, codepoints, codepointIndexByOffset, cpHashByOffset,
                out EntityHandle[] cpSequence, cpHandleByOffset);
            if (!gcHandlesByHash.TryGetValue(gcHash, out EntityHandle gcEntity))
            {
                gcEntity = batch.AddEntity(gcHash, "grapheme_cluster");
                batch.AddSignificance(gcEntity, "source_authority", trustMu);
                for (int i = 0; i < cpSequence.Length; i++)
                {
                    batch.AddSequence(gcEntity, cpSequence[i], i, 1);
                }
                gcHandlesByHash[gcHash] = gcEntity;
            }

            gcHandleByOffset[gc.ByteOffset] = gcEntity;
            gcHashByOffset[gc.ByteOffset] = gcHash;
        }

        // ── Stage 3: word_form entities (UAX #29 word boundaries) ───────
        // Each word_form composes its constituent grapheme clusters in order.
        // Same surface form across decomposers → same Merkle hash → one entity.
        List<WordRange> words = WordBoundaries.EnumerateWords(utf8, properties);
        Dictionary<byte[], EntityHandle> wordHandlesByHash = new(ByteArrayEqualityComparer.Instance);
        Dictionary<long, EntityHandle> wordHandleByOffset = new(words.Count);
        Dictionary<long, byte[]> wordHashByOffset = new(words.Count);
        Dictionary<long, int> wordByteLengthByOffset = new(words.Count);

        int graphemeIdx = 0;
        foreach (WordRange word in words)
        {
            List<EntityHandle> childHandles = new();
            List<byte[]> childHashes = new();

            while (graphemeIdx < graphemes.Count)
            {
                GraphemeRange gc = graphemes[graphemeIdx];
                if (gc.ByteOffset + gc.ByteLength <= word.ByteOffset)
                {
                    graphemeIdx++;
                    continue;
                }
                if (gc.ByteOffset >= word.ByteOffset + word.ByteLength)
                {
                    break;
                }
                if (gcHandleByOffset.TryGetValue(gc.ByteOffset, out EntityHandle gcHandle) &&
                    gcHashByOffset.TryGetValue(gc.ByteOffset, out byte[]? gcHash))
                {
                    childHandles.Add(gcHandle);
                    childHashes.Add(gcHash);
                }
                graphemeIdx++;
            }

            if (childHashes.Count == 0)
            {
                continue;
            }

            byte[] wordHash = ComputeMerkleHash(childHashes);
            if (!wordHandlesByHash.TryGetValue(wordHash, out EntityHandle wordEntity))
            {
                wordEntity = batch.AddEntity(wordHash, "word_form");
                batch.AddSignificance(wordEntity, "source_authority", trustMu);
                for (int i = 0; i < childHandles.Count; i++)
                {
                    batch.AddSequence(wordEntity, childHandles[i], i, 1);
                }
                wordHandlesByHash[wordHash] = wordEntity;
            }

            wordHandleByOffset[word.ByteOffset] = wordEntity;
            wordHashByOffset[word.ByteOffset] = wordHash;
            wordByteLengthByOffset[word.ByteOffset] = word.ByteLength;
        }

        // ── Stage 4: text_composition (word_forms + raw spans in order) ─
        // Raw spans are the bytes between word boundaries (whitespace,
        // punctuation). Stored as nested compositions of codepoint entities so
        // round-trip recomposition is byte-identical.
        Dictionary<byte[], EntityHandle> rawSpanHandlesByHash = new(ByteArrayEqualityComparer.Instance);
        List<EntityHandle> textChildHandles = new();
        List<byte[]> textChildHashes = new();
        long cursor = 0;

        foreach (WordRange word in words)
        {
            AppendRawSpanIfAny(
                batch, codepoints, codepointIndexByOffset, cpHandleByOffset, cpHashByOffset,
                rawSpanHandlesByHash, trustMu,
                cursor, word.ByteOffset, textChildHandles, textChildHashes);

            if (wordHandleByOffset.TryGetValue(word.ByteOffset, out EntityHandle wHandle) &&
                wordHashByOffset.TryGetValue(word.ByteOffset, out byte[]? wHash))
            {
                textChildHandles.Add(wHandle);
                textChildHashes.Add(wHash);
                cursor = word.ByteOffset + wordByteLengthByOffset[word.ByteOffset];
            }
            else
            {
                cursor = word.ByteOffset + word.ByteLength;
            }
        }

        AppendRawSpanIfAny(
            batch, codepoints, codepointIndexByOffset, cpHandleByOffset, cpHashByOffset,
            rawSpanHandlesByHash, trustMu,
            cursor, utf8.Length, textChildHandles, textChildHashes);

        byte[] rootHash = ComputeMerkleHash(textChildHashes);
        EntityHandle rootEntity = batch.AddEntity(rootHash, entityType);
        batch.AddSignificance(rootEntity, "source_authority", trustMu);
        for (int i = 0; i < textChildHandles.Count; i++)
        {
            batch.AddSequence(rootEntity, textChildHandles[i], i, 1);
        }
        return (rootEntity, rootHash);
    }

    private static List<(int Codepoint, int ByteOffset, int ByteLength)> DecodeCodepoints(ReadOnlySpan<byte> utf8)
    {
        List<(int, int, int)> result = new(utf8.Length);
        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (consumed == 0)
            {
                break;
            }
            result.Add((cp, idx, consumed));
            idx += consumed;
        }
        return result;
    }

    private static byte[] HashCodepoint(int cpValue)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(cpValue >> 24);
        bytes[1] = (byte)(cpValue >> 16);
        bytes[2] = (byte)(cpValue >> 8);
        bytes[3] = (byte)cpValue;
        return Blake3.Hash(bytes);
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

    private static byte[] ComputeGraphemeClusterHash(
        GraphemeRange gc,
        List<(int Codepoint, int ByteOffset, int ByteLength)> codepoints,
        Dictionary<long, int> codepointIndexByOffset,
        Dictionary<long, byte[]> cpHashByOffset,
        out EntityHandle[] cpSequence,
        Dictionary<long, EntityHandle> cpHandleByOffset)
    {
        int startIdx = codepointIndexByOffset[gc.ByteOffset];
        long endOffset = gc.ByteOffset + gc.ByteLength;
        List<byte[]> hashes = new(gc.CodepointLength);
        List<EntityHandle> handles = new(gc.CodepointLength);

        int idx = startIdx;
        while (idx < codepoints.Count)
        {
            (_, int cpOffset, int cpLen) = codepoints[idx];
            if (cpOffset >= endOffset)
            {
                break;
            }
            hashes.Add(cpHashByOffset[cpOffset]);
            handles.Add(cpHandleByOffset[cpOffset]);
            idx++;
        }
        cpSequence = handles.ToArray();
        return ComputeMerkleHash(hashes);
    }

    private static void AppendRawSpanIfAny(
        IIngestionBatch batch,
        List<(int Codepoint, int ByteOffset, int ByteLength)> codepoints,
        Dictionary<long, int> codepointIndexByOffset,
        Dictionary<long, EntityHandle> cpHandleByOffset,
        Dictionary<long, byte[]> cpHashByOffset,
        Dictionary<byte[], EntityHandle> rawSpanHandlesByHash,
        double trustMu,
        long startOffset,
        long endOffset,
        List<EntityHandle> targetHandles,
        List<byte[]> targetHashes)
    {
        if (endOffset <= startOffset)
        {
            return;
        }
        if (!codepointIndexByOffset.TryGetValue(startOffset, out int idx))
        {
            return;
        }

        List<EntityHandle> childHandles = new();
        List<byte[]> childHashes = new();
        while (idx < codepoints.Count)
        {
            (_, int cpOffset, int cpLen) = codepoints[idx];
            if (cpOffset >= endOffset)
            {
                break;
            }
            if (cpOffset + cpLen > endOffset)
            {
                return;
            }
            childHandles.Add(cpHandleByOffset[cpOffset]);
            childHashes.Add(cpHashByOffset[cpOffset]);
            idx++;
        }
        if (childHashes.Count == 0)
        {
            return;
        }
        if (childHashes.Count == 1)
        {
            targetHandles.Add(childHandles[0]);
            targetHashes.Add(childHashes[0]);
            return;
        }

        byte[] spanHash = ComputeMerkleHash(childHashes);
        if (!rawSpanHandlesByHash.TryGetValue(spanHash, out EntityHandle spanEntity))
        {
            spanEntity = batch.AddEntity(spanHash, "text_composition");
            batch.AddSignificance(spanEntity, "source_authority", trustMu);
            for (int i = 0; i < childHandles.Count; i++)
            {
                batch.AddSequence(spanEntity, childHandles[i], i, 1);
            }
            rawSpanHandlesByHash[spanHash] = spanEntity;
        }
        targetHandles.Add(spanEntity);
        targetHashes.Add(spanHash);
    }
}
