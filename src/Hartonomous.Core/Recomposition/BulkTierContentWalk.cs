using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Native;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Client-side bulk-tier content walk over the substrate's
/// mantissa-packed LINESTRINGZM physicality manifest. Replaces the
/// PG-side recursive-CTE walkers
/// (<c>substrate.recompose_text</c> / <c>recompose_content</c> /
/// <c>get_composition_children</c> / <c>pg_recompose_walk</c>) per
/// modular-wishing-koala Gate 1 reopened item #36.
///
/// <para>
/// Strategy: cache every (entity → mantissa-packed-geom) and (104-bit prefix
/// → 32-byte hash) the document touches, using TWO PG queries per tier (one
/// geom fetch, one prefix resolve, both bulked across the distinct hashes at
/// that tier). Then walk the in-memory cache DFS-order, emitting codepoint
/// UTF-8 bytes directly. Round-trip count is O(tier depth + 1), not
/// O(node count). For text (max 5–6 tiers: document → paragraph →
/// text_composition → word_form → grapheme_cluster → codepoint), any size
/// of document recomposes in &lt;1 s.
/// </para>
/// </summary>
public static class BulkTierContentWalk
{
    private const byte EwkbLittleEndianByteOrder = 0x01;
    private const uint WkbZFlag      = 0x80000000;
    private const uint WkbMFlag      = 0x40000000;
    private const uint WkbLineString = 2u;
    private const uint EwkbLineStringZM = WkbLineString | WkbZFlag | WkbMFlag;
    private const uint EwkbSridFlag = 0x20000000;
    private const int LineStringZmHeaderBytes = 1 + 4 + 4;

    /// <summary>
    /// 2^51 sentinel: vertex X mantissas above this carry packed child-hash
    /// prefixes; below this they are real atom coordinates.
    /// </summary>
    private const double PackedVertexMinX = 2.0 * (1L << 50);

    /// <summary>
    /// Recompose UTF-8 byte content for <paramref name="rootHash"/> via the
    /// bulk-tier walk. <paramref name="conn"/> must be an open connection;
    /// the walk does NOT open / close it.
    /// </summary>
    public static async Task<byte[]> RecomposeAsync(
        NpgsqlConnection conn,
        Hash32 rootHash,
        int maxDepth,
        CancellationToken ct)
    {
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth), maxDepth, "maxDepth must be >= 1.");
        }

        // Root short-circuit: atom standing alone.
        int rootCp = ResolveCodepointFromHash(rootHash);
        if (rootCp > 0)
        {
            return EncodeCodepoint(rootCp);
        }

        // Two caches: geom-by-entity-hash and hash32-by-prefix.
        // Built tier-by-tier; consulted by the streaming walk at the end.
        Dictionary<Hash32, byte[]?> geomCache = new();
        Dictionary<(long lo, long hi), Hash32> prefixCache = new();
        HashSet<Hash32> codepointSet = new();
        HashSet<Hash32> nonCodepointSet = new();

        // Tier expansion: BFS over distinct unique non-codepoint hashes.
        HashSet<Hash32> currentTier = [rootHash];
        nonCodepointSet.Add(rootHash);
        for (int depth = 0; depth < maxDepth && currentTier.Count > 0; depth++)
        {
            ct.ThrowIfCancellationRequested();

            // Bulk fetch geoms for everything in this tier.
            await FetchGeomsAsync(conn, currentTier, geomCache, ct);

            // Parse all the slots collected this tier, accumulate distinct prefixes.
            HashSet<(long, long)> tierPrefixes = new();
            foreach (Hash32 parent in currentTier)
            {
                if (!geomCache.TryGetValue(parent, out byte[]? geom) || geom is null)
                {
                    continue;
                }
                AddSlotPrefixes(geom, tierPrefixes);
            }

            if (tierPrefixes.Count == 0)
            {
                break;
            }

            // Bulk resolve all NEW prefixes to hash32 (skip ones we already
            // resolved from a previous tier).
            HashSet<(long, long)> needsResolve = new();
            foreach ((long, long) p in tierPrefixes)
            {
                if (!prefixCache.ContainsKey(p))
                {
                    needsResolve.Add(p);
                }
            }
            if (needsResolve.Count > 0)
            {
                await ResolvePrefixesAsync(conn, needsResolve, prefixCache, ct);
            }

            // Classify resolved children: codepoint leaf vs non-leaf
            // (a codepoint never has its own composition; non-codepoints go
            // into the next tier).
            HashSet<Hash32> nextTier = new();
            foreach ((long, long) p in tierPrefixes)
            {
                if (!prefixCache.TryGetValue(p, out Hash32 h))
                {
                    continue;
                }
                if (codepointSet.Contains(h) || nonCodepointSet.Contains(h))
                {
                    continue;
                }
                int cp = ResolveCodepointFromHash(h);
                if (cp > 0)
                {
                    codepointSet.Add(h);
                }
                else
                {
                    nonCodepointSet.Add(h);
                    nextTier.Add(h);
                }
            }

            currentTier = nextTier;
        }

        // Streaming DFS walk through the in-memory caches, emitting codepoint
        // UTF-8 bytes in content order. Stack-based to avoid recursion depth
        // limits on deep documents.
        MemoryStream output = new(capacity: 1 << 17);
        WriteWalk(rootHash, geomCache, prefixCache, codepointSet, output, maxDepth);
        return output.ToArray();
    }

    /// <summary>
    /// Streaming DFS walk using an explicit stack. At each non-leaf node,
    /// look up its packed geometry, sort vertex slots by ordinal, push
    /// children right-to-left so leftmost-first DFS emits in content order.
    /// </summary>
    private static void WriteWalk(
        Hash32 root,
        Dictionary<Hash32, byte[]?> geomCache,
        Dictionary<(long, long), Hash32> prefixCache,
        HashSet<Hash32> codepointSet,
        MemoryStream output,
        int maxDepth)
    {
        // Each stack frame: (hash, expansionRepetition, expansionTotal,
        // alreadyExpanded). To minimize allocations, the simplest pattern
        // is to materialize child arrays as we go.
        Stack<WalkFrame> stack = new(capacity: 64);
        stack.Push(new WalkFrame(root, Depth: 0));

        // Reusable buffers.
        List<VertexSlot> tmpSlots = new(capacity: 256);

        while (stack.Count > 0)
        {
            WalkFrame frame = stack.Pop();
            Hash32 h = frame.Hash;

            if (codepointSet.Contains(h))
            {
                int cp = ResolveCodepointFromHash(h);
                if (cp > 0)
                {
                    WriteCodepoint(cp, output);
                }
                continue;
            }

            if (frame.Depth >= maxDepth)
            {
                continue;
            }

            if (!geomCache.TryGetValue(h, out byte[]? geom) || geom is null)
            {
                continue;
            }

            tmpSlots.Clear();
            ParsePackedLineString(geom, tmpSlots);
            if (tmpSlots.Count == 0)
            {
                continue;
            }

            tmpSlots.Sort(static (a, b) =>
            {
                int c = a.Ordinal.CompareTo(b.Ordinal);
                return c != 0 ? c : a.VertexIndex.CompareTo(b.VertexIndex);
            });

            // Push children right-to-left so leftmost is popped first.
            // RLE expansion: a slot with rle_count=N pushes N stack frames.
            // For DFS-left-first emission, push higher repetition indices
            // first so they emit later.
            int childDepth = frame.Depth + 1;
            for (int i = tmpSlots.Count - 1; i >= 0; i--)
            {
                VertexSlot slot = tmpSlots[i];
                if (!prefixCache.TryGetValue((slot.HashLow, slot.HashHi), out Hash32 childHash))
                {
                    continue;
                }
                int rle = Math.Max(1, slot.Rle);
                for (int rep = rle - 1; rep >= 0; rep--)
                {
                    stack.Push(new WalkFrame(childHash, childDepth));
                }
            }
        }
    }

    private static void WriteCodepoint(int cp, MemoryStream output)
    {
        // Inline UTF-8 encode of one codepoint. Up to 4 bytes.
        if (cp <= 0x7F)
        {
            output.WriteByte((byte)cp);
        }
        else if (cp <= 0x7FF)
        {
            output.WriteByte((byte)(0xC0 | (cp >> 6)));
            output.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
        else if (cp <= 0xFFFF)
        {
            output.WriteByte((byte)(0xE0 | (cp >> 12)));
            output.WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
            output.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
        else
        {
            output.WriteByte((byte)(0xF0 | (cp >> 18)));
            output.WriteByte((byte)(0x80 | ((cp >> 12) & 0x3F)));
            output.WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
            output.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
    }

    /// <summary>
    /// Bulk-fetch the mantissa-packed LINESTRINGZM geom for every distinct
    /// entity hash in <paramref name="parentHashes"/> that isn't already in
    /// <paramref name="geomCache"/>. One PG query per call.
    /// </summary>
    private static async Task FetchGeomsAsync(
        NpgsqlConnection conn,
        HashSet<Hash32> parentHashes,
        Dictionary<Hash32, byte[]?> geomCache,
        CancellationToken ct)
    {
        List<Hash32> need = new();
        foreach (Hash32 h in parentHashes)
        {
            if (!geomCache.ContainsKey(h))
            {
                need.Add(h);
            }
        }
        if (need.Count == 0)
        {
            return;
        }

        byte[][] hashes = new byte[need.Count][];
        for (int i = 0; i < need.Count; i++)
        {
            hashes[i] = need[i].ToByteArray();
        }

        // Pre-seed every input with null so callers can detect "parent had
        // no packed geometry" (codepoint atom or content-less placeholder)
        // without re-asking the DB.
        for (int i = 0; i < need.Count; i++)
        {
            geomCache[need[i]] = null;
        }

        // physicality_type_id IN (3, 15) restricts to composition partitions
        // (content + entity_shape). Atom (1) and firefly (2) partitions are
        // skipped — they carry POINTZM and never have packed manifests.
        // The composition partitions' CHECK constraints guarantee
        // LINESTRING/MULTILINESTRING geometries with packed mantissas, so
        // we don't need GeometryType / ST_NumPoints / ST_X gates on the
        // PG side; the C# parser already shrugs off any unpacked vertex.
        const string Sql = @"
SELECT p.entity_hash, ST_AsEWKB(p.geom) AS geom
  FROM substrate.physicality p
 WHERE p.physicality_type_id IN (3, 15)
   AND p.entity_hash = ANY($1)";
        await using NpgsqlCommand cmd = new(Sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = hashes,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
        });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[] hashBytes = (byte[])reader.GetValue(0);
            byte[] geom = (byte[])reader.GetValue(1);
            Hash32 h = new(hashBytes);

            // Keep first packed geometry seen per entity (Merkle invariant
            // guarantees identical content per hash).
            byte[]? current = geomCache[h];
            if (current is null)
            {
                geomCache[h] = geom;
            }
        }
    }

    /// <summary>
    /// Bulk-resolve a set of (hashLow52, hashHi52) prefixes to full 32-byte
    /// hashes. One PG round-trip via the composite (hash_bits_0_51,
    /// hash_bits_52_103) btree.
    /// </summary>
    private static async Task ResolvePrefixesAsync(
        NpgsqlConnection conn,
        HashSet<(long, long)> distinct,
        Dictionary<(long, long), Hash32> prefixCache,
        CancellationToken ct)
    {
        long[] los = new long[distinct.Count];
        long[] his = new long[distinct.Count];
        int idx = 0;
        foreach ((long lo, long hi) in distinct)
        {
            los[idx] = lo;
            his[idx] = hi;
            idx++;
        }

        // Post-substrate.entity-revert: the GENERATED hash_bits_0_51 /
        // hash_bits_52_103 columns are gone (substrate.entity is identity-
        // only — hash PK). The composite btree index now indexes the
        // functional expressions substrate.bb_hash_lo(hash) +
        // substrate.bb_hash_hi(hash); the lookup uses those functions in
        // the JOIN condition so the index can still drive a point-lookup.
        const string Sql = @"
SELECT e.hash, substrate.bb_hash_lo(e.hash) AS hash_bits_0_51,
       substrate.bb_hash_hi(e.hash) AS hash_bits_52_103
  FROM substrate.entity e
  JOIN unnest($1::bigint[], $2::bigint[]) AS u(lo, hi)
    ON substrate.bb_hash_lo(e.hash) = u.lo
   AND substrate.bb_hash_hi(e.hash) = u.hi";
        await using NpgsqlCommand cmd = new(Sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = los,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = his,
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
        });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[] hash = (byte[])reader.GetValue(0);
            long lo = reader.GetInt64(1);
            long hi = reader.GetInt64(2);
            prefixCache[(lo, hi)] = new Hash32(hash);
        }
    }

    /// <summary>
    /// Parse a PostGIS EWKB LINESTRINGZM payload and append its vertex slots
    /// to <paramref name="dest"/>. Inverse of
    /// <c>Hartonomous.Engine.Ingestion.Geometry4dPayloadBuilder.LineString</c>.
    /// Handles the singleton-doubled layout (a 1-child composition emits 2
    /// identical vertices because PostGIS rejects single-vertex LINESTRINGs).
    /// </summary>
    public static void ParsePackedLineString(byte[] ewkb, List<VertexSlot> dest)
    {
        if (ewkb.Length < LineStringZmHeaderBytes)
        {
            return;
        }

        ReadOnlySpan<byte> payload = ewkb;
        if (payload[0] != EwkbLittleEndianByteOrder)
        {
            return;
        }

        uint typeCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        int cursor = 5;
        if ((typeCode & EwkbSridFlag) != 0)
        {
            if (payload.Length < cursor + 4 + 4)
            {
                return;
            }
            cursor += 4;
            typeCode &= ~EwkbSridFlag;
        }

        if (typeCode != EwkbLineStringZM)
        {
            return;
        }

        if (payload.Length < cursor + 4)
        {
            return;
        }
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        if (n == 0 || payload.Length < cursor + 32 * (int)n)
        {
            return;
        }

        bool doubled = false;
        if (n == 2)
        {
            doubled = AllFourCoordsEqual(payload.Slice(cursor, 32), payload.Slice(cursor + 32, 32));
        }

        int emitCount = doubled ? 1 : (int)n;
        int offset = cursor;
        for (int i = 0; i < emitCount; i++)
        {
            double x = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(offset, 8));
            double y = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(offset + 8, 8));
            double z = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(offset + 16, 8));
            offset += 32;

            if (x <= PackedVertexMinX)
            {
                continue;
            }

            long hashLo = MantissaPacking.UnpackHashLo(x);
            long hashHi = MantissaPacking.UnpackHashHi(z);
            int ordinal = MantissaPacking.UnpackOrdinal(y);
            int rle = MantissaPacking.UnpackRle(y);

            dest.Add(new VertexSlot(hashLo, hashHi, ordinal, rle, i));
        }
    }

    private static void AddSlotPrefixes(byte[] ewkb, HashSet<(long, long)> dest)
    {
        if (ewkb.Length < LineStringZmHeaderBytes)
        {
            return;
        }
        if (ewkb[0] != EwkbLittleEndianByteOrder)
        {
            return;
        }
        uint typeCode = BinaryPrimitives.ReadUInt32LittleEndian(ewkb.AsSpan(1, 4));
        int cursor = 5;
        if ((typeCode & EwkbSridFlag) != 0)
        {
            if (ewkb.Length < cursor + 4 + 4)
            {
                return;
            }
            cursor += 4;
            typeCode &= ~EwkbSridFlag;
        }
        if (typeCode != EwkbLineStringZM)
        {
            return;
        }
        if (ewkb.Length < cursor + 4)
        {
            return;
        }
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(ewkb.AsSpan(cursor, 4));
        cursor += 4;
        if (n == 0 || ewkb.Length < cursor + 32 * (int)n)
        {
            return;
        }
        bool doubled = false;
        if (n == 2)
        {
            doubled = AllFourCoordsEqual(ewkb.AsSpan(cursor, 32), ewkb.AsSpan(cursor + 32, 32));
        }
        int emitCount = doubled ? 1 : (int)n;
        int offset = cursor;
        for (int i = 0; i < emitCount; i++)
        {
            double x = BinaryPrimitives.ReadDoubleLittleEndian(ewkb.AsSpan(offset, 8));
            double z = BinaryPrimitives.ReadDoubleLittleEndian(ewkb.AsSpan(offset + 16, 8));
            offset += 32;
            if (x <= PackedVertexMinX)
            {
                continue;
            }
            dest.Add((MantissaPacking.UnpackHashLo(x), MantissaPacking.UnpackHashHi(z)));
        }
    }

    /// <summary>
    /// Compatibility shim for callers that want a returned list rather than
    /// appending to one. Not used internally; kept for any external tests.
    /// </summary>
    public static List<VertexSlot> ParsePackedLineString(byte[] ewkb)
    {
        List<VertexSlot> result = new();
        ParsePackedLineString(ewkb, result);
        return result;
    }

    private static bool AllFourCoordsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        return BinaryPrimitives.ReadDoubleLittleEndian(a.Slice(0, 8)) == BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(0, 8))
            && BinaryPrimitives.ReadDoubleLittleEndian(a.Slice(8, 8)) == BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(8, 8))
            && BinaryPrimitives.ReadDoubleLittleEndian(a.Slice(16, 8)) == BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(16, 8))
            && BinaryPrimitives.ReadDoubleLittleEndian(a.Slice(24, 8)) == BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(24, 8));
    }

    private static unsafe int ResolveCodepointFromHash(Hash32 hash)
    {
        Span<byte> bytes = stackalloc byte[Hash32.Length];
        hash.CopyTo(bytes);
        fixed (byte* p = bytes)
        {
            return TextDecomposeNative.UcdCpFromHash(p);
        }
    }

    private static byte[] EncodeCodepoint(int cp)
    {
        Span<byte> bytes = stackalloc byte[4];
        int written;
        if (cp <= 0x7F)
        {
            bytes[0] = (byte)cp;
            written = 1;
        }
        else if (cp <= 0x7FF)
        {
            bytes[0] = (byte)(0xC0 | (cp >> 6));
            bytes[1] = (byte)(0x80 | (cp & 0x3F));
            written = 2;
        }
        else if (cp <= 0xFFFF)
        {
            bytes[0] = (byte)(0xE0 | (cp >> 12));
            bytes[1] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            bytes[2] = (byte)(0x80 | (cp & 0x3F));
            written = 3;
        }
        else
        {
            bytes[0] = (byte)(0xF0 | (cp >> 18));
            bytes[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
            bytes[2] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            bytes[3] = (byte)(0x80 | (cp & 0x3F));
            written = 4;
        }
        byte[] result = new byte[written];
        bytes[..written].CopyTo(result);
        return result;
    }

    private readonly record struct WalkFrame(Hash32 Hash, int Depth);
}
