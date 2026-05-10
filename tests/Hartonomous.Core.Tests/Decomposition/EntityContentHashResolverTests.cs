using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Text;

namespace Hartonomous.Core.Tests.Decomposition;

public sealed class EntityContentHashResolverTests
{
    [Fact]
    public void GetCandidateHashes_Lemma_IncludesFlatAndMerkleHashes()
    {
        IReadOnlyList<byte[]> hashes = EntityContentHashResolver.GetCandidateHashes(
            "cat", ["lemma"]);

        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeFlatHash("cat")));
        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeWordFormHash("cat")));
    }

    [Fact]
    public void GetCandidateHashes_CapitalizedLemma_IncludesLowercaseCandidates()
    {
        IReadOnlyList<byte[]> hashes = EntityContentHashResolver.GetCandidateHashes(
            "The", ["lemma"]);

        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeFlatHash("The")));
        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeWordFormHash("The")));
        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeFlatHash("the")));
        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(ComputeWordFormHash("the")));
    }

    [Fact]
    public void GetCandidateHashes_SingleCodepoint_IncludesCodepointHashWhenRequested()
    {
        IReadOnlyList<byte[]> hashes = EntityContentHashResolver.GetCandidateHashes(
            "A", ["codepoint"]);

        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(HashCodepoint('A')));
        Assert.Contains(hashes, h => h.AsSpan().SequenceEqual(HashCodepoint('a')));
    }

    [Fact]
    public void GetCandidateHashes_MultiRuneCodepointRequest_DoesNotIncludeCodepointHash()
    {
        IReadOnlyList<byte[]> hashes = EntityContentHashResolver.GetCandidateHashes(
            "ab", ["codepoint"]);

        Assert.DoesNotContain(hashes, h => h.AsSpan().SequenceEqual(HashCodepoint('a')));
        Assert.DoesNotContain(hashes, h => h.AsSpan().SequenceEqual(HashCodepoint('b')));
    }

    private static byte[] ComputeFlatHash(string content)
    {
        return Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan());
    }

    private static byte[] ComputeWordFormHash(string form)
        => SubstrateTextDecomposer.ComputeRootHash(
            Encoding.UTF8.GetBytes(form).AsSpan(),
            "word_form");

    private static byte[] HashCodepoint(int cpValue)
    {
        Span<byte> cpBytes = stackalloc byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return Blake3.Hash(cpBytes);
    }

}
