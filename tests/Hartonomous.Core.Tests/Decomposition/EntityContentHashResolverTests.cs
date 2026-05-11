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
        IReadOnlyList<Hash32> hashes = EntityContentHashResolver.GetCandidateHashes(
            "cat", ["lemma"]);

        Assert.Contains(hashes, h => h.Equals(ComputeFlatHash("cat")));
        Assert.Contains(hashes, h => h.Equals(ComputeWordFormHash("cat")));
    }

    [Fact]
    public void GetCandidateHashes_CapitalizedLemma_IncludesLowercaseCandidates()
    {
        IReadOnlyList<Hash32> hashes = EntityContentHashResolver.GetCandidateHashes(
            "The", ["lemma"]);

        Assert.Contains(hashes, h => h.Equals(ComputeFlatHash("The")));
        Assert.Contains(hashes, h => h.Equals(ComputeWordFormHash("The")));
        Assert.Contains(hashes, h => h.Equals(ComputeFlatHash("the")));
        Assert.Contains(hashes, h => h.Equals(ComputeWordFormHash("the")));
    }

    [Fact]
    public void GetCandidateHashes_SingleCodepoint_IncludesCodepointHashWhenRequested()
    {
        IReadOnlyList<Hash32> hashes = EntityContentHashResolver.GetCandidateHashes(
            "A", ["codepoint"]);

        Assert.Contains(hashes, h => h.Equals(HashCodepoint('A')));
        Assert.Contains(hashes, h => h.Equals(HashCodepoint('a')));
    }

    [Fact]
    public void GetCandidateHashes_MultiRuneCodepointRequest_DoesNotIncludeCodepointHash()
    {
        IReadOnlyList<Hash32> hashes = EntityContentHashResolver.GetCandidateHashes(
            "ab", ["codepoint"]);

        Assert.DoesNotContain(hashes, h => h.Equals(HashCodepoint('a')));
        Assert.DoesNotContain(hashes, h => h.Equals(HashCodepoint('b')));
    }

    private static Hash32 ComputeFlatHash(string content)
    {
        return Blake3.Hash32(Encoding.UTF8.GetBytes(content).AsSpan());
    }

    private static Hash32 ComputeWordFormHash(string form)
        => SubstrateTextDecomposer.ComputeRootHash(
            Encoding.UTF8.GetBytes(form).AsSpan(),
            "word_form");

    private static Hash32 HashCodepoint(int cpValue)
    {
        Span<byte> cpBytes = stackalloc byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return Blake3.Hash32(cpBytes);
    }

}
