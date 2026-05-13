using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class Blake3
{
    public const int HashLen = 32;

    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output32)
    {
        if (output32.Length != HashLen)
        {
            throw new ComputeArgumentException($"Blake3.Hash output buffer must be {HashLen} bytes");
        }
        NativeCompute.Blake3(input, (nuint)input.Length, output32);
    }

    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[HashLen];
        NativeCompute.Blake3(input, (nuint)input.Length, output);
        return output;
    }

    public static Hash32 Hash32(ReadOnlySpan<byte> input)
    {
        Span<byte> output = stackalloc byte[HashLen];
        NativeCompute.Blake3(input, (nuint)input.Length, output);
        return new Hash32(output);
    }

    /// <summary>
    /// Batched BLAKE3 for N inputs in a single FFI call. Eliminates per-record
    /// P/Invoke trampoline cost — for 1M short inputs, this is ~1ms vs ~500ms
    /// of marshalling for 1M scalar Hash() calls. Internally OpenMP-parallel
    /// across inputs; each input uses the AVX-512 single-shot path.
    ///
    /// inputs: read-only sequence of N input buffers (pinned for the duration
    /// of the call via GCHandle).
    /// output: caller-allocated; must be N * HashLen bytes. Row i is the
    /// hash of inputs[i].
    /// </summary>
    public static unsafe void HashMany(
        ReadOnlySpan<ReadOnlyMemory<byte>> inputs,
        Span<byte> output)
    {
        int n = inputs.Length;
        if (output.Length != n * HashLen)
        {
            throw new ComputeArgumentException(
                $"Blake3.HashMany: output buffer must be {n * HashLen} bytes (got {output.Length})");
        }
        if (n == 0)
        {
            return;
        }

        // Pin every input so the native side sees stable pointers for the
        // duration of the call. MemoryHandle's lifetime owns the pinning;
        // we release every handle in finally.
        MemoryHandle[] handles = new MemoryHandle[n];
        try
        {
            byte*[] ptrs = new byte*[n];
            nuint[] lens = new nuint[n];
            for (int i = 0; i < n; i++)
            {
                handles[i] = inputs[i].Pin();
                ptrs[i] = (byte*)handles[i].Pointer;
                lens[i] = (nuint)inputs[i].Length;
            }

            fixed (byte** pPtrs = ptrs)
            fixed (nuint* pLens = lens)
            fixed (byte* pOut = output)
            {
                int rc = NativeCompute.Blake3Many(pPtrs, pLens, n, pOut);
                if (rc != 0)
                {
                    throw new ComputeException($"Blake3.HashMany returned {rc}");
                }
            }
        }
        finally
        {
            for (int i = 0; i < n; i++)
            {
                handles[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Convenience overload: returns N freshly-allocated 32-byte arrays.
    /// Use the Span overload above for hot paths to avoid per-call allocation.
    /// </summary>
    public static byte[][] HashMany(ReadOnlySpan<ReadOnlyMemory<byte>> inputs)
    {
        int n = inputs.Length;
        byte[] flat = new byte[n * HashLen];
        HashMany(inputs, flat);
        byte[][] result = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            result[i] = new byte[HashLen];
            flat.AsSpan(i * HashLen, HashLen).CopyTo(result[i]);
        }
        return result;
    }

    public static Hash32[] HashMany32(ReadOnlySpan<ReadOnlyMemory<byte>> inputs)
    {
        int n = inputs.Length;
        byte[] flat = ArrayPool<byte>.Shared.Rent(n * HashLen);
        try
        {
            Span<byte> output = flat.AsSpan(0, n * HashLen);
            HashMany(inputs, output);
            Hash32[] result = new Hash32[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = new Hash32(output.Slice(i * HashLen, HashLen));
            }
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flat);
        }
    }

    /// <summary>
    /// BLAKE3 hash of an <b>atomic identifier</b> string (AP-9). Reserved for
    /// language codes, POS codes, semantic-relation codes, namespace tags —
    /// strings whose role is to identify a slot in a bounded reference
    /// vocabulary. NOT for user-visible text: every text-bearing content
    /// (codepoint, grapheme, word_form, sentence, paragraph) routes through
    /// <c>SubstrateTextDecomposer.Emit</c> so cross-source consensus
    /// accumulates on one content-addressed identity per content. Hashing
    /// user text directly here fragments the substrate.
    /// </summary>
    public static Hash32 ComputeAtomicStringHash(string atomicIdentifier)
        => Hash32(Encoding.UTF8.GetBytes(atomicIdentifier).AsSpan());

    /// <summary>
    /// Canonical edge identity: BLAKE3 over the 4-byte big-endian
    /// <paramref name="edgeTypeId"/> followed by the role-ordered
    /// 32-byte participant hashes. Matches the substrate-side identity
    /// computation in <c>substrate.edge</c> so cross-model corroboration on
    /// the same logical relation collapses to one edge row.
    /// </summary>
    public static Hash32 ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<Hash32> participantHashes)
    {
        int len = 4 + participantHashes.Length * HashLen;
        Span<byte> buffer = participantHashes.Length <= 8
            ? stackalloc byte[len]
            : new byte[len];
        BitConverter.TryWriteBytes(buffer, edgeTypeId);
        for (int i = 0; i < participantHashes.Length; i++)
        {
            participantHashes[i].CopyTo(buffer.Slice(4 + i * HashLen, HashLen));
        }
        return Hash32(buffer);
    }

    /// <summary>
    /// Content hash for a Unicode codepoint atom. 4 big-endian bytes →
    /// BLAKE3. Shared across every decomposer so the same codepoint from
    /// ISO 639-3, WordNet, Wiktionary, every safetensors tokenizer, and any
    /// user prompt deduplicates to the one tier-0 atom in the substrate's
    /// Merkle DAG.
    /// </summary>
    public static Hash32 HashCodepoint(int codepoint)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(codepoint >> 24);
        bytes[1] = (byte)(codepoint >> 16);
        bytes[2] = (byte)(codepoint >> 8);
        bytes[3] = (byte)codepoint;
        return Hash32(bytes);
    }

    /// <summary>
    /// Split a BLAKE3 hash into two 52-bit BIGINT halves (lo = bits 0..51,
    /// hi = bits 52..103) for use as ingestion-trajectory vertex X+Z
    /// coordinates. Mirrors <c>substrate.bb_hash_lo(bytea)</c> /
    /// <c>substrate.bb_hash_hi(bytea)</c> byte-for-byte so the C# write side
    /// and the SQL read side produce / consume identical doubles.
    /// </summary>
    public static (long Lo, long Hi) HashPrefix104(Hash32 hash)
    {
        Span<byte> bytes = stackalloc byte[HashLen];
        hash.CopyTo(bytes);
        const ulong Mask52 = 0x000F_FFFF_FFFF_FFFFUL;
        long lo = (long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8)) & Mask52);
        long hi = (long)((BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(6, 8)) >> 4) & Mask52);
        return (lo, hi);
    }
}
