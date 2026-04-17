using System;
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
}
