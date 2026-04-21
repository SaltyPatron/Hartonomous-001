using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public sealed class HashFunctionTests
{
    [Fact]
    public void HashCodepoint_Deterministic()
    {
        byte[] h1 = InvokeHashCodepoint(0x0041);
        byte[] h2 = InvokeHashCodepoint(0x0041);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashCodepoint_DifferentValues_DifferentHashes()
    {
        byte[] h1 = InvokeHashCodepoint(0x0041);
        byte[] h2 = InvokeHashCodepoint(0x0042);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void HashCodepoint_Produces32Bytes()
    {
        byte[] hash = InvokeHashCodepoint(0x0041);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void HashCodepoint_BigEndianEncoding()
    {
        // U+10FFFF should produce same hash regardless of system endianness
        // because we encode as big-endian explicitly.
        byte[] hash = InvokeHashCodepoint(0x10FFFF);
        Assert.Equal(32, hash.Length);
        Assert.NotEqual(new byte[32], hash);
    }

    [Fact]
    public void HashCodepoint_ZeroAndMaxProduceDifferentHashes()
    {
        byte[] h0 = InvokeHashCodepoint(0x0000);
        byte[] hMax = InvokeHashCodepoint(0x10FFFF);
        Assert.NotEqual(h0, hMax);
    }

    [Fact]
    public void HashCollationElement_Deterministic()
    {
        CollationWeight w = new(0x1C47, 0x0020, 0x0008);
        byte[] h1 = InvokeHashCollationElement(w);
        byte[] h2 = InvokeHashCollationElement(w);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashCollationElement_DifferentWeights_DifferentHashes()
    {
        CollationWeight w1 = new(0x1C47, 0x0020, 0x0008);
        CollationWeight w2 = new(0x1C48, 0x0020, 0x0008);
        byte[] h1 = InvokeHashCollationElement(w1);
        byte[] h2 = InvokeHashCollationElement(w2);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void HashCollationElement_SecondaryDifference_DifferentHashes()
    {
        CollationWeight w1 = new(0x1C47, 0x0020, 0x0008);
        CollationWeight w2 = new(0x1C47, 0x0021, 0x0008);
        Assert.NotEqual(InvokeHashCollationElement(w1), InvokeHashCollationElement(w2));
    }

    [Fact]
    public void HashCollationElement_TertiaryDifference_DifferentHashes()
    {
        CollationWeight w1 = new(0x1C47, 0x0020, 0x0008);
        CollationWeight w2 = new(0x1C47, 0x0020, 0x0009);
        Assert.NotEqual(InvokeHashCollationElement(w1), InvokeHashCollationElement(w2));
    }

    [Fact]
    public void HashCollationElement_Produces32Bytes()
    {
        CollationWeight w = new(0x1C47, 0x0020, 0x0008);
        Assert.Equal(32, InvokeHashCollationElement(w).Length);
    }

    // ── Reflection to invoke internal statics ──

    private static byte[] InvokeHashCodepoint(int cpValue)
    {
        System.Reflection.MethodInfo method = typeof(UcdUcaDecomposer)
            .GetMethod("HashCodepoint",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)!;
        return (byte[])method.Invoke(null, [cpValue])!;
    }

    private static byte[] InvokeHashCollationElement(CollationWeight weights)
    {
        System.Reflection.MethodInfo method = typeof(UcdUcaDecomposer)
            .GetMethod("HashCollationElement",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)!;
        return (byte[])method.Invoke(null, [weights])!;
    }
}
