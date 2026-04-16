using System;
using Hartonomous.Core.Native;

namespace Hartonomous.Core.Tests.Native;

public sealed class Blake3NativeTests
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
    public void EmptyInput_MatchesOfficialVector()
    {
        Span<byte> hash = stackalloc byte[Blake3Native.HashLen];
        Blake3Native.Blake3(ReadOnlySpan<byte>.Empty, 0, hash);

        Assert.Equal(
            "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
            ToHex(hash));
    }

    [Fact]
    public void SingleZeroByte_MatchesOfficialVector()
    {
        ReadOnlySpan<byte> input = stackalloc byte[] { 0x00 };
        Span<byte> hash = stackalloc byte[Blake3Native.HashLen];
        Blake3Native.Blake3(input, (nuint)input.Length, hash);

        Assert.Equal(
            "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213",
            ToHex(hash));
    }

    [Fact]
    public void Ramp1024_MatchesOfficialVector()
    {
        byte[] input = new byte[1024];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 251);
        }
        Span<byte> hash = stackalloc byte[Blake3Native.HashLen];
        Blake3Native.Blake3(input, (nuint)input.Length, hash);

        Assert.Equal(
            "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7",
            ToHex(hash));
    }
}
