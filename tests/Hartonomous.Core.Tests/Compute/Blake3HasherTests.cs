using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Tests.Compute;

public sealed class Blake3HasherTests
{
    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789abcdef";
        Span<char> chars = stackalloc char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2]     = digits[bytes[i] >> 4];
            chars[i * 2 + 1] = digits[bytes[i] & 0x0f];
        }
        return new string(chars);
    }

    [Fact]
    public void Empty_MatchesOfficialVector()
    {
        Blake3Hasher h = Blake3Hasher.Create();
        Span<byte> digest = stackalloc byte[Blake3.HashLen];
        h.Finalize(digest);
        Assert.Equal(
            "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
            ToHex(digest));
    }

    [Fact]
    public void Ramp1024_Streamed_MatchesOneShot()
    {
        byte[] input = new byte[1024];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 251);
        }
        byte[] oneShot = Blake3.Hash(input);

        // Feed in 7 arbitrary chunks — result must equal the one-shot digest.
        int[] splits = [0, 1, 5, 64, 200, 701, 900, 1024];
        Blake3Hasher h = Blake3Hasher.Create();
        for (int s = 0; s < splits.Length - 1; s++)
        {
            h.Update(input.AsSpan(splits[s], splits[s + 1] - splits[s]));
        }
        byte[] streamed = h.Finalize();
        Assert.Equal(ToHex(oneShot), ToHex(streamed));
    }

    [Fact]
    public void LargeRandomBuffer_Streamed_MatchesOneShot()
    {
        // 8 MiB — exercises internal block boundaries + big chunks
        byte[] input = new byte[8 * 1024 * 1024];
        Random rng = new(1234);
        rng.NextBytes(input);

        byte[] oneShot = Blake3.Hash(input);

        Blake3Hasher h = Blake3Hasher.Create();
        const int chunk = 1 << 20; // 1 MiB
        for (int off = 0; off < input.Length; off += chunk)
        {
            int n = Math.Min(chunk, input.Length - off);
            h.Update(input.AsSpan(off, n));
        }
        byte[] streamed = h.Finalize();
        Assert.Equal(ToHex(oneShot), ToHex(streamed));
    }
}
