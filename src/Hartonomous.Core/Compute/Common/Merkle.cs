using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class Merkle
{
    /// <summary>
    /// BLAKE3 hash of an ordered concatenation of 32-byte child hashes. Caller is
    /// responsible for choosing the canonical child ordering (typically by hash).
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> childHashes32, Span<byte> output32)
    {
        if (output32.Length != Blake3.HashLen)
        {
            throw new ComputeArgumentException($"Merkle.Hash output buffer must be {Blake3.HashLen} bytes");
        }
        if (childHashes32.Length % Blake3.HashLen != 0)
        {
            throw new ComputeArgumentException(
                $"Merkle.Hash input must be a multiple of {Blake3.HashLen} bytes");
        }
        nuint childCount = (nuint)(childHashes32.Length / Blake3.HashLen);
        NativeError.ThrowIfError(
            NativeCompute.Blake3Merkle(childHashes32, childCount, output32),
            "blake3_merkle");
    }

    public static byte[] Hash(ReadOnlySpan<byte> childHashes32)
    {
        byte[] output = new byte[Blake3.HashLen];
        Hash(childHashes32, output);
        return output;
    }

    public static Hash32 Hash32(ReadOnlySpan<byte> childHashes32)
    {
        Span<byte> output = stackalloc byte[Blake3.HashLen];
        Hash(childHashes32, output);
        return new Hash32(output);
    }

    public static Hash32 Hash32(ReadOnlySpan<Hash32> childHashes)
    {
        Span<byte> stack = childHashes.Length <= 64
            ? stackalloc byte[childHashes.Length * Blake3.HashLen]
            : new byte[childHashes.Length * Blake3.HashLen];
        for (int i = 0; i < childHashes.Length; i++)
        {
            childHashes[i].CopyTo(stack.Slice(i * Blake3.HashLen, Blake3.HashLen));
        }
        return Hash32(stack);
    }
}
