using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="Merkle.Hash"/>. Mirrors
/// ext/libhartonomous/tests/test_merkle.cc.
/// </summary>
public sealed class MerkleTests
{
    [Fact]
    public void Empty_EqualsBlake3OfEmpty()
    {
        byte[] direct = Blake3.Hash(ReadOnlySpan<byte>.Empty);
        byte[] merkle = Merkle.Hash(ReadOnlySpan<byte>.Empty);
        Assert.Equal(direct, merkle);
    }

    [Fact]
    public void SingleChild_EqualsBlake3OfThatChild()
    {
        byte[] child = new byte[Blake3.HashLen];
        for (int i = 0; i < child.Length; i++) { child[i] = (byte)i; }
        byte[] direct = Blake3.Hash(child);
        byte[] merkle = Merkle.Hash(child);
        Assert.Equal(direct, merkle);
    }

    [Fact]
    public void OrderSensitive_AbDiffersFromBa()
    {
        byte[] ab = new byte[2 * Blake3.HashLen];
        byte[] ba = new byte[2 * Blake3.HashLen];
        for (int i = 0; i < Blake3.HashLen; i++)
        {
            ab[i] = 0x11;
            ab[Blake3.HashLen + i] = 0x22;
            ba[i] = 0x22;
            ba[Blake3.HashLen + i] = 0x11;
        }
        byte[] h1 = Merkle.Hash(ab);
        byte[] h2 = Merkle.Hash(ba);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Determinism_SameInput_ByteIdenticalOutput()
    {
        byte[] kids = new byte[4 * Blake3.HashLen];
        for (int i = 0; i < kids.Length; i++) { kids[i] = (byte)(i * 7 + 3); }
        byte[] a = Merkle.Hash(kids);
        byte[] b = Merkle.Hash(kids);
        Assert.Equal(a, b);
    }
}
