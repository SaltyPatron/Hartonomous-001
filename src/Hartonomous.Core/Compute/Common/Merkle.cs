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
}
