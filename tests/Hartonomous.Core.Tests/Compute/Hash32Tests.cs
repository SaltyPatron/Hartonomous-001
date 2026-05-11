using Hartonomous.Core.Compute.Common;
using Xunit;

namespace Hartonomous.Core.Tests.Compute;

public sealed class Hash32Tests
{
    [Fact]
    public void Constructor_rejects_non_32_byte_input()
    {
        Assert.Throws<ArgumentException>(() => new Hash32(new byte[31]));
        Assert.Throws<ArgumentException>(() => new Hash32(new byte[33]));
    }

    [Fact]
    public void Constructor_copies_source_bytes()
    {
        byte[] source = new byte[Hash32.Length];
        source[0] = 0xAB;

        Hash32 hash = new(source);
        source[0] = 0x00;

        Assert.Equal(0xAB, hash.ToByteArray()[0]);
    }

    [Fact]
    public void CopyTo_and_hex_export_are_stable()
    {
        byte[] source = new byte[Hash32.Length];
        source[0] = 0x01;
        source[^1] = 0xFE;
        Hash32 hash = new(source);

        Span<byte> destination = stackalloc byte[Hash32.Length];
        hash.CopyTo(destination);

        Assert.Equal(source, destination.ToArray());
        Assert.Equal(Convert.ToHexString(source), hash.ToHexString());
        Assert.Equal(hash.ToHexString(), hash.ToString());
    }

    [Fact]
    public void Value_equality_supports_sets_and_sorting()
    {
        byte[] aBytes = new byte[Hash32.Length];
        byte[] bBytes = new byte[Hash32.Length];
        aBytes[^1] = 1;
        bBytes[^1] = 2;
        Hash32 a = new(aBytes);
        Hash32 b = new(bBytes);

        HashSet<Hash32> set = [a, new Hash32(a.ToByteArray()), b];
        Hash32[] sorted = [b, a];
        Array.Sort(sorted);

        Assert.Equal(2, set.Count);
        Assert.Equal([a, b], sorted);
    }
}
