using Hartonomous.Core;
using Hartonomous.Engine.Ingestion;

namespace Hartonomous.Engine.Tests.Ingestion;

public sealed class NpgsqlIngestionPipelineTests
{
    [Theory]
    [InlineData("entity_pos", "pos_id")]
    [InlineData("entity_sense", "sense_id")]
    [InlineData("entity_language", "language_id")]
    [InlineData("entity_morph_feature", "morph_feature_id")]
    [InlineData("model_architecture_class", "architecture_class_id")]
    [InlineData("tensor_tensor_role", "tensor_role_id")]
    [InlineData("pattern_deprel", "deprel_id")]
    public void GetJunctionRefColumn_KnownTables_ReturnsCorrectColumn(string table, string expected)
    {
        string result = InvokeGetJunctionRefColumn(table);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetJunctionRefColumn_UnknownTable_Throws()
    {
        Assert.Throws<ArgumentException>(() => InvokeGetJunctionRefColumn("nonexistent_table"));
    }

    [Fact]
    public void GetJunctionRefColumn_WideTable_Throws()
    {
        Assert.Throws<ArgumentException>(() => InvokeGetJunctionRefColumn("codepoint_property"));
    }

    [Fact]
    public void GetJunctionRefColumn_SqlInjectionAttempt_Throws()
    {
        Assert.Throws<ArgumentException>(() => InvokeGetJunctionRefColumn("entity_pos; DROP TABLE--"));
    }

    [Fact]
    public void ByteArrayComparer_EqualArrays_AreEqual()
    {
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 2, 3, 4];

        var comparer = ByteArrayEqualityComparer.Instance;
        Assert.True(comparer.Equals(a, b));
        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
    }

    [Fact]
    public void ByteArrayComparer_DifferentArrays_AreNotEqual()
    {
        byte[] a = [1, 2, 3, 4];
        byte[] b = [1, 2, 3, 5];

        var comparer = ByteArrayEqualityComparer.Instance;
        Assert.False(comparer.Equals(a, b));
    }

    [Fact]
    public void ByteArrayComparer_SameReference_AreEqual()
    {
        byte[] a = [1, 2, 3];
        var comparer = ByteArrayEqualityComparer.Instance;
        Assert.True(comparer.Equals(a, a));
    }

    [Fact]
    public void ByteArrayComparer_NullHandling()
    {
        var comparer = ByteArrayEqualityComparer.Instance;
        Assert.False(comparer.Equals(null, [1]));
        Assert.False(comparer.Equals([1], null));
        Assert.True(comparer.Equals(null, null));
    }

    [Fact]
    public void ByteArrayComparer_EmptyArrays_AreEqual()
    {
        var comparer = ByteArrayEqualityComparer.Instance;
        Assert.True(comparer.Equals([], []));
    }

    [Fact]
    public void ByteArrayComparer_WorksInDictionary()
    {
        var comparer = ByteArrayEqualityComparer.Instance;
        Dictionary<byte[], long> dict = new(comparer);

        byte[] key = [0xDE, 0xAD, 0xBE, 0xEF];
        dict[key] = 42L;

        byte[] lookupKey = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.True(dict.TryGetValue(lookupKey, out long value));
        Assert.Equal(42L, value);
    }

    [Fact]
    public void ComputeEdgeHash_Deterministic()
    {
        long[] members = [100L, 200L];
        byte[] hash1 = InvokeComputeEdgeHash(1, members);
        byte[] hash2 = InvokeComputeEdgeHash(1, members);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeEdgeHash_DifferentTypeId_DifferentHash()
    {
        long[] members = [100L, 200L];
        byte[] hash1 = InvokeComputeEdgeHash(1, members);
        byte[] hash2 = InvokeComputeEdgeHash(2, members);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeEdgeHash_DifferentMembers_DifferentHash()
    {
        byte[] hash1 = InvokeComputeEdgeHash(1, [100L, 200L]);
        byte[] hash2 = InvokeComputeEdgeHash(1, [100L, 300L]);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeEdgeHash_MemberOrderMatters()
    {
        byte[] hash1 = InvokeComputeEdgeHash(1, [100L, 200L]);
        byte[] hash2 = InvokeComputeEdgeHash(1, [200L, 100L]);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeEdgeHash_Produces32Bytes()
    {
        byte[] hash = InvokeComputeEdgeHash(1, [100L]);
        Assert.Equal(32, hash.Length);
    }

    // ── Reflection helpers to test private/internal static methods ──

    private static string InvokeGetJunctionRefColumn(string table)
    {
        System.Reflection.MethodInfo method = typeof(NpgsqlIngestionPipeline)
            .GetMethod("GetJunctionRefColumn",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            return (string)method.Invoke(null, [table])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }

    private static byte[] InvokeComputeEdgeHash(int edgeTypeId, long[] memberEntityIds)
    {
        System.Reflection.MethodInfo method = typeof(NpgsqlIngestionPipeline)
            .GetMethod("ComputeEdgeHash",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            return (byte[])method.Invoke(null, [edgeTypeId, memberEntityIds])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
