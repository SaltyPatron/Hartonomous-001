using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Text;

/// <summary>
/// THE canonical text decomposer. The single, authoritative implementation
/// of <c>UTF-8 bytes → substrate content recording</c>. Every text-bearing
/// content from any decomposer (WordNet glosses/lemmas/synsets, Wiktionary
/// lemmas/etymology/pronunciation/translation/example, OMW foreign lemmas,
/// UD tokens/sentences, Tatoeba sentences, Safetensors model artifacts,
/// prompts) routes through <see cref="Emit"/>. One implementation, one
/// hash per content, full Merkle DAG plus 4D geometry plus structure
/// preserved every time.
///
/// This replaces the four prior text-decomposition surfaces:
///   Hartonomous.Core.Text.Segmentation.TextSegmentationEmitter.EmitTextComposition
///   Hartonomous.Core.Decomposition.BaseDecomposer.EmitWordFormMerkle
///   Hartonomous.Core.Decomposition.BaseDecomposer.EmitLemmaMaybeCompound
///   Hartonomous.Decomposers.Text.TextDecomposer.IngestUtf8DocumentIntoBatch
///
/// All four produced different hashes for the same content. <see cref="Emit"/>
/// produces ONE hash per byte sequence, deterministically.
///
/// Design lock per docs/specs/text-decomposer-unification.md §4:
/// <list type="bullet">
///   <item>UAX #29 grapheme-cluster boundaries via <see cref="GraphemeClusters.Enumerate"/>
///     (substrate-table-driven; no <c>StringInfo</c>, no platform variance)</item>
///   <item>UAX #29 word boundaries via <see cref="WordBoundaries.EnumerateWords"/></item>
///   <item>BLAKE3 Merkle at every layer — codepoint hash = BLAKE3(big-endian
///     4-byte rune); grapheme_cluster hash = Merkle of codepoint hashes;
///     word_form hash = Merkle of grapheme_cluster hashes; composition
///     hash = Merkle of child hashes in linear order</item>
///   <item>4D physicality at every layer — POINTZM for codepoints (UCA
///     Super-Fibonacci S³ projection), LINESTRINGZM contour for multi-cp
///     grapheme clusters, LINESTRINGZM trajectory for word_forms and
///     compositions; centroid memoized per-hash</item>
///   <item>Sequence rows at every parent→child layer, run-length-encoded
///     so a refrain of three identical sentences collapses to one row
///     with <c>rle_count = 3</c></item>
///   <item>Significance prior in <c>source_authority</c> arena per layer</item>
///   <item>Per-call provenance routing via <see cref="TextDecomposeOptions.ProvenanceCode"/></item>
///   <item>Determinism: same UTF-8 input always produces byte-identical
///     substrate state. Cross-decomposer dedup is automatic — content IS
///     the entity, regardless of which caller emitted it.</item>
/// </list>
///
/// The result is structurally complete: byte-perfect round-trip recomposition,
/// every layer indexed, every centroid precomputed, every sequence row
/// addressable, every trajectory available for Fréchet / Hausdorff comparison
/// against any other content's trajectory.
/// </summary>
public static class CanonicalTextDecomposer
{
    /// <summary>
    /// Decompose <paramref name="utf8"/> into the substrate's text DAG and
    /// emit every entity, sequence row, physicality row, and significance
    /// row to <paramref name="batch"/>. See class summary for the contract.
    /// </summary>
    public static TextDecomposeResult Emit(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        ICodepointProperties codepointProperties,
        TextDecomposeOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(codepointProperties);

        Counters c = default;
        // Track which hashes have already received physicality / significance
        // emission inside THIS batch so we don't emit the same row twice.
        // (The substrate's ON CONFLICT DO NOTHING also handles cross-batch dedup,
        // but in-batch dedup keeps producer round-trips and staging size down.)
        HashSet<HashKey> physicalityEmitted = new(HashKey.Comparer);
        HashSet<HashKey> significanceEmitted = new(HashKey.Comparer);

        // Empty input → emit a degenerate empty composition entity. Its hash
        // is BLAKE3.Merkle of zero children = BLAKE3 of empty bytes. Caller
        // gets a stable handle for round-trip and dedup purposes.
        if (utf8.Length == 0)
        {
            return EmitEmptyComposition(batch, options, ref c);
        }

        // ── Stage 0: decode codepoints ─────────────────────────────────────
        List<CodepointDecode> codepoints = DecodeCodepoints(utf8);
        if (codepoints.Count == 0)
        {
            // All-malformed UTF-8: degenerate to empty composition.
            return EmitEmptyComposition(batch, options, ref c);
        }

        // ── Stage 1: emit codepoint atoms ──────────────────────────────────
        // Each codepoint becomes:
        //   * an entity with hash = BLAKE3(big-endian 4-byte rune value)
        //   * a POINTZM physicality at its UCA Super-Fibonacci S³ position
        //     (memoized: the codepoint 'd' has ONE physicality row regardless
        //     of how many sentences contain it)
        //   * a source_authority significance prior
        //   * the codepoint_property junction is populated by the UCD/UCA
        //     decomposer at seed time, not here — but inference can JOIN
        //     against it given a codepoint hash, so this hash IS the bridge
        EntityHandle[] cpHandles = new EntityHandle[codepoints.Count];
        byte[][] cpHashes = new byte[codepoints.Count][];
        (double X, double Y, double Z, double M)[] cpCentroids =
            new (double, double, double, double)[codepoints.Count];

        for (int i = 0; i < codepoints.Count; i++)
        {
            int rune = codepoints[i].Codepoint;
            byte[] hash = HashCodepoint(rune);
            EntityHandle handle = batch.AddEntity(hash, "codepoint");
            cpHandles[i] = handle;
            cpHashes[i] = hash;

            (double X, double Y, double Z, double M) pos =
                PhysicalityEmitter.CodepointS3Position(rune);
            cpCentroids[i] = pos;

            HashKey key = new(hash);
            if (physicalityEmitted.Add(key))
            {
                batch.AddPhysicalityPoint4d(handle, "s3_position", pos.X, pos.Y, pos.Z, pos.M);
                c.PhysicalityRows++;
            }
            if (significanceEmitted.Add(key))
            {
                batch.AddSignificance(handle, "source_authority", options.TrustMu);
                c.SignificanceRows++;
            }
            c.Entities++;
        }

        // ── Stage 2: enumerate UAX #29 grapheme clusters ───────────────────
        // GraphemeClusters.Enumerate is the substrate-table-driven UAX #29
        // implementation (uses codepoint_property.gcb_id), NOT .NET's
        // StringInfo.GetTextElementEnumerator. Cross-platform deterministic.
        List<GraphemeRange> graphemeRanges =
            GraphemeClusters.Enumerate(utf8, codepointProperties);

        // For each grapheme cluster:
        //   * find its constituent codepoints (by byte offset slice)
        //   * hash = BLAKE3.Merkle of codepoint hashes in left-to-right order
        //   * single-cp graphemes degenerate to the codepoint's POINTZM as centroid
        //   * multi-cp graphemes get a contour LINESTRINGZM through codepoint centroids
        //   * sequence: grapheme → codepoint at each ordinal, RLE-compressed
        //   * source_authority significance prior
        EntityHandle[] gcHandles = new EntityHandle[graphemeRanges.Count];
        byte[][] gcHashes = new byte[graphemeRanges.Count][];
        (double X, double Y, double Z, double M)[] gcCentroids =
            new (double, double, double, double)[graphemeRanges.Count];

        // Build offset → codepoint-index map once for O(1) range slicing.
        Dictionary<long, int> cpIndexByByteOffset = new(codepoints.Count);
        for (int i = 0; i < codepoints.Count; i++)
        {
            cpIndexByByteOffset[codepoints[i].ByteOffset] = i;
        }

        for (int gi = 0; gi < graphemeRanges.Count; gi++)
        {
            GraphemeRange gr = graphemeRanges[gi];
            int firstCp = cpIndexByByteOffset[gr.ByteOffset];
            int gcCpCount = gr.CodepointLength;

            // Slice the codepoint hashes belonging to this grapheme cluster.
            byte[] gcHash = ComputeMerkleHashSlice(cpHashes, firstCp, gcCpCount);
            EntityHandle gcHandle = batch.AddEntity(gcHash, "grapheme_cluster");
            gcHandles[gi] = gcHandle;
            gcHashes[gi] = gcHash;

            // Centroid: 1-cp grapheme = codepoint POINTZM; multi-cp = mean of
            // codepoint POINTZMs along the cluster.
            (double X, double Y, double Z, double M) gcCentroid;
            if (gcCpCount == 1)
            {
                gcCentroid = cpCentroids[firstCp];
            }
            else
            {
                Span<(double X, double Y, double Z, double M)> verts =
                    cpCentroids.AsSpan(firstCp, gcCpCount);
                gcCentroid = BaseDecomposer.MeanCentroid(verts);

                // Multi-cp graphemes get a contour LINESTRINGZM stored in
                // physicality. (Single-cp graphemes don't — their centroid IS
                // the codepoint's POINTZM, already memoized at codepoint level.)
                HashKey gcKey = new(gcHash);
                if (physicalityEmitted.Add(gcKey))
                {
                    batch.AddPhysicalityLineString4d(gcHandle, "contour", verts);
                    c.PhysicalityRows++;
                }
            }
            gcCentroids[gi] = gcCentroid;

            // Sequence rows: grapheme → codepoint at each ordinal, RLE-compressed.
            EmitChildrenRle(batch, gcHandle, cpHandles, cpHashes, firstCp, gcCpCount, ref c);

            HashKey sigKey = new(gcHash);
            if (significanceEmitted.Add(sigKey))
            {
                batch.AddSignificance(gcHandle, "source_authority", options.TrustMu);
                c.SignificanceRows++;
            }
            c.Entities++;
        }

        // ── Stage 3: enumerate UAX #29 word boundaries ─────────────────────
        // WordBoundaries.EnumerateWords returns runs of grapheme clusters
        // grouped into AlphaNumeric / Numeric / Other / etc. The "Other"
        // ranges (whitespace, punctuation between words) are emitted as
        // raw_span text_compositions so byte-identical round-trip recompose
        // works. AlphaNumeric / language-specific ranges are emitted as
        // word_form entities.
        List<WordRange> wordRanges =
            WordBoundaries.EnumerateWords(utf8, codepointProperties);

        Dictionary<long, int> gcIndexByByteOffset = new(graphemeRanges.Count);
        for (int gi = 0; gi < graphemeRanges.Count; gi++)
        {
            gcIndexByByteOffset[graphemeRanges[gi].ByteOffset] = gi;
        }

        // Each word range covers some prefix of grapheme clusters starting
        // at gr.ByteOffset == word.ByteOffset. We walk forward grapheme by
        // grapheme until we reach word.ByteOffset + word.ByteLength.
        List<EntityHandle> compositionChildHandles = new(wordRanges.Count);
        List<byte[]> compositionChildHashes = new(wordRanges.Count);

        for (int wi = 0; wi < wordRanges.Count; wi++)
        {
            WordRange wr = wordRanges[wi];
            int firstGc = gcIndexByByteOffset[wr.ByteOffset];
            int gcCount = CountGraphemesInWord(graphemeRanges, firstGc, wr);

            string entityType = wr.Kind == WordKind.Other
                ? "text_composition"  // raw spans (whitespace/punct) — recursive composition
                : "word_form";

            byte[] wfHash = ComputeMerkleHashSlice(gcHashes, firstGc, gcCount);
            EntityHandle wfHandle = batch.AddEntity(wfHash, entityType);

            // Word_form trajectory = LINESTRINGZM through grapheme_cluster
            // centroids. Single-grapheme word_form degenerates to that
            // grapheme's centroid (already memoized).
            (double X, double Y, double Z, double M) wfCentroid;
            if (gcCount == 1)
            {
                wfCentroid = gcCentroids[firstGc];
            }
            else
            {
                Span<(double X, double Y, double Z, double M)> verts =
                    gcCentroids.AsSpan(firstGc, gcCount);
                wfCentroid = BaseDecomposer.MeanCentroid(verts);

                HashKey wfKey = new(wfHash);
                if (physicalityEmitted.Add(wfKey))
                {
                    batch.AddPhysicalityLineString4d(wfHandle, "contour", verts);
                    c.PhysicalityRows++;
                }
            }

            // Sequence rows: word_form / raw_span → grapheme_cluster, RLE-compressed.
            EmitChildrenRle(batch, wfHandle, gcHandles, gcHashes, firstGc, gcCount, ref c);

            HashKey wfSigKey = new(wfHash);
            if (significanceEmitted.Add(wfSigKey))
            {
                batch.AddSignificance(wfHandle, "source_authority", options.TrustMu);
                c.SignificanceRows++;
            }
            c.Entities++;

            compositionChildHandles.Add(wfHandle);
            compositionChildHashes.Add(wfHash);
        }

        // ── Stage 4: top-level composition ─────────────────────────────────
        // TopEntityType is caller-specified: "text_composition" for prompts,
        // Tatoeba sentences, free-form text; "lemma" for WordNet/Wiktionary
        // lemmas; "language_name" for ISO 639 names; etc. All such entities
        // are content-pure compositions over their word_form / raw_span
        // children, so the same surface form ALWAYS produces the same hash
        // regardless of caller-declared type.
        //
        // (Note: under the planned hash-only PK + classification junction
        // schema (Phase C), the type is recorded as a classification on the
        // single entity row, not as part of identity. Here we still emit
        // (entity_type_id, hash) pairs because the schema isn't yet
        // collapsed; once it is, change AddEntity's signature accordingly.)
        byte[] compositionHash = ComputeMerkleHash(compositionChildHashes);
        EntityHandle compositionHandle = batch.AddEntity(compositionHash, options.TopEntityType);
        c.Entities++;

        // Top-level trajectory through child centroids.
        (double X, double Y, double Z, double M) compositionCentroid;
        if (compositionChildHandles.Count == 1)
        {
            // Single-word composition — centroid is the word's centroid.
            compositionCentroid = wordRanges.Count > 0
                ? GetWordCentroid(wordRanges[0], graphemeRanges, gcIndexByByteOffset, gcCentroids)
                : default;
        }
        else if (wordRanges.Count == 0)
        {
            // Whitespace-only / punctuation-only / empty input: composition
            // entity exists (so callers always have a handle to refer to)
            // but has no geometric trajectory through word_form centroids.
            // Skip physicality emission. Centroid defaults to origin.
            compositionCentroid = default;
        }
        else
        {
            (double X, double Y, double Z, double M)[] verts =
                new (double, double, double, double)[wordRanges.Count];
            for (int wi = 0; wi < wordRanges.Count; wi++)
            {
                verts[wi] = GetWordCentroid(wordRanges[wi], graphemeRanges, gcIndexByByteOffset, gcCentroids);
            }
            compositionCentroid = BaseDecomposer.MeanCentroid(verts);

            HashKey ckey = new(compositionHash);
            if (physicalityEmitted.Add(ckey))
            {
                batch.AddPhysicalityLineString4d(compositionHandle, "contour", verts);
                c.PhysicalityRows++;
            }
        }

        // Sequence rows: composition → child at ordinal, RLE-compressed.
        EmitCompositionChildrenRle(
            batch, compositionHandle,
            compositionChildHandles, compositionChildHashes,
            ref c);

        HashKey compSigKey = new(compositionHash);
        if (significanceEmitted.Add(compSigKey))
        {
            batch.AddSignificance(compositionHandle, "source_authority", options.TrustMu);
            c.SignificanceRows++;
        }

        return new TextDecomposeResult(
            compositionHandle,
            compositionHash,
            c.Entities,
            c.SequenceRows,
            c.PhysicalityRows,
            c.SignificanceRows,
            compositionCentroid);
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private struct Counters
    {
        public long Entities;
        public long SequenceRows;
        public long PhysicalityRows;
        public long SignificanceRows;
    }

    private readonly record struct CodepointDecode(int Codepoint, long ByteOffset, int ByteLength);

    /// <summary>
    /// Wrapper struct for byte[] hashes so they can be used as dictionary keys
    /// via structural (content) equality rather than reference equality.
    /// </summary>
    private readonly struct HashKey : IEquatable<HashKey>
    {
        public static readonly IEqualityComparer<HashKey> Comparer = EqualityComparer<HashKey>.Default;

        private readonly byte[] _bytes;
        public HashKey(byte[] bytes) { _bytes = bytes; }

        public bool Equals(HashKey other)
        {
            if (_bytes.Length != other._bytes.Length) { return false; }
            for (int i = 0; i < _bytes.Length; i++)
            {
                if (_bytes[i] != other._bytes[i]) { return false; }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is HashKey k && Equals(k);

        public override int GetHashCode()
        {
            // FNV-1a over the bytes. Deterministic; good enough for in-process
            // dictionaries (not used as substrate identity).
            int h = unchecked((int)2166136261u);
            for (int i = 0; i < _bytes.Length; i++)
            {
                h = unchecked((h ^ _bytes[i]) * 16777619);
            }
            return h;
        }
    }

    private static List<CodepointDecode> DecodeCodepoints(ReadOnlySpan<byte> utf8)
    {
        List<CodepointDecode> result = new(utf8.Length);
        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (consumed == 0)
            {
                // Malformed UTF-8 byte — caller's input is invalid. Skip the
                // byte and continue (fail-tolerant for robustness; alternative
                // is throw, but most callers feed prompts/text from external
                // sources where malformed bytes can occur). The substrate's
                // Law #6 (determinism) holds because the skip behavior is
                // deterministic.
                idx++;
                continue;
            }
            result.Add(new CodepointDecode(cp, idx, consumed));
            idx += consumed;
        }
        return result;
    }

    private static byte[] HashCodepoint(int rune)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(rune >> 24);
        bytes[1] = (byte)(rune >> 16);
        bytes[2] = (byte)(rune >> 8);
        bytes[3] = (byte)rune;
        return Blake3.Hash(bytes);
    }

    /// <summary>
    /// Compute BLAKE3.Merkle over a slice of child hashes [start, start+count).
    /// Each child hash is 32 bytes; concatenate in order then hash.
    /// </summary>
    private static byte[] ComputeMerkleHashSlice(byte[][] childHashes, int start, int count)
    {
        byte[] concat = new byte[count * Blake3.HashLen];
        for (int i = 0; i < count; i++)
        {
            childHashes[start + i].CopyTo(concat.AsSpan(i * Blake3.HashLen));
        }
        return Merkle.Hash(concat.AsSpan());
    }

    private static byte[] ComputeMerkleHash(List<byte[]> childHashes)
    {
        if (childHashes.Count == 0)
        {
            // Empty Merkle = BLAKE3 of zero bytes. Stable, well-defined.
            return Merkle.Hash(ReadOnlySpan<byte>.Empty);
        }
        byte[] concat = new byte[childHashes.Count * Blake3.HashLen];
        for (int i = 0; i < childHashes.Count; i++)
        {
            childHashes[i].CopyTo(concat.AsSpan(i * Blake3.HashLen));
        }
        return Merkle.Hash(concat.AsSpan());
    }

    /// <summary>
    /// Emit sequence rows from <paramref name="parent"/> to
    /// <c>childHandles[start..start+count)</c>, compressing consecutive runs
    /// of identical children into a single row with <c>rle_count = run_length</c>.
    /// Three identical sentences in a row collapse to one sequence row;
    /// lookup at any ordinal in the run still hits the row via
    /// <c>ordinal &lt;= N AND ordinal + rle_count &gt; N</c>.
    /// </summary>
    private static void EmitChildrenRle(
        IIngestionBatch batch,
        EntityHandle parent,
        EntityHandle[] childHandles,
        byte[][] childHashes,
        int start, int count,
        ref Counters c)
    {
        if (count == 0) { return; }
        int runStart = 0;        // index within [0, count)
        byte[] runHash = childHashes[start + 0];
        for (int i = 1; i <= count; i++)
        {
            bool atEnd = i == count;
            bool diff = !atEnd && !HashesEqual(childHashes[start + i], runHash);
            if (atEnd || diff)
            {
                int ordinal = runStart + 1;          // 1-based per substrate convention
                int rleCount = i - runStart;
                batch.AddSequence(parent, ordinal, childHandles[start + runStart], rleCount);
                c.SequenceRows++;
                if (!atEnd)
                {
                    runStart = i;
                    runHash = childHashes[start + i];
                }
            }
        }
    }

    private static void EmitCompositionChildrenRle(
        IIngestionBatch batch,
        EntityHandle parent,
        List<EntityHandle> childHandles,
        List<byte[]> childHashes,
        ref Counters c)
    {
        int count = childHandles.Count;
        if (count == 0) { return; }
        int runStart = 0;
        byte[] runHash = childHashes[0];
        for (int i = 1; i <= count; i++)
        {
            bool atEnd = i == count;
            bool diff = !atEnd && !HashesEqual(childHashes[i], runHash);
            if (atEnd || diff)
            {
                int ordinal = runStart + 1;
                int rleCount = i - runStart;
                batch.AddSequence(parent, ordinal, childHandles[runStart], rleCount);
                c.SequenceRows++;
                if (!atEnd)
                {
                    runStart = i;
                    runHash = childHashes[i];
                }
            }
        }
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) { return false; }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) { return false; }
        }
        return true;
    }

    private static int CountGraphemesInWord(
        List<GraphemeRange> graphemeRanges, int firstGc, WordRange word)
    {
        long endByte = word.ByteOffset + word.ByteLength;
        int count = 0;
        for (int i = firstGc; i < graphemeRanges.Count; i++)
        {
            GraphemeRange gr = graphemeRanges[i];
            if (gr.ByteOffset >= endByte) { break; }
            count++;
        }
        return count;
    }

    private static (double X, double Y, double Z, double M) GetWordCentroid(
        WordRange word,
        List<GraphemeRange> graphemeRanges,
        Dictionary<long, int> gcIndexByByteOffset,
        (double X, double Y, double Z, double M)[] gcCentroids)
    {
        int firstGc = gcIndexByByteOffset[word.ByteOffset];
        int count = CountGraphemesInWord(graphemeRanges, firstGc, word);
        if (count == 1) { return gcCentroids[firstGc]; }
        Span<(double X, double Y, double Z, double M)> verts = gcCentroids.AsSpan(firstGc, count);
        return BaseDecomposer.MeanCentroid(verts);
    }

    private static TextDecomposeResult EmitEmptyComposition(
        IIngestionBatch batch, TextDecomposeOptions options, ref Counters c)
    {
        byte[] emptyHash = Merkle.Hash(ReadOnlySpan<byte>.Empty);
        EntityHandle handle = batch.AddEntity(emptyHash, options.TopEntityType);
        batch.AddSignificance(handle, "source_authority", options.TrustMu);
        c.Entities++;
        c.SignificanceRows++;
        return new TextDecomposeResult(
            handle,
            emptyHash,
            c.Entities,
            c.SequenceRows,
            c.PhysicalityRows,
            c.SignificanceRows,
            (0, 0, 0, 0));
    }
}
