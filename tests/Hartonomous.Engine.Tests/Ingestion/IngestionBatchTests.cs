using System;
using Hartonomous.Core.Ingestion;
using Hartonomous.Engine.Ingestion;
using Xunit;

namespace Hartonomous.Engine.Tests.Ingestion;

/// <summary>
/// Smoke tests for the hash-as-PK <see cref="IngestionBatch"/>. The prior
/// suite tested RemapHandle / ResolveHandle / GetEntityHash, all deleted
/// in the greenfield rewrite — handles ARE the foreign keys, no resolve
/// step exists. The substantial behavioural tests live in the integration
/// test project where they exercise a real Postgres against the v2 schema.
/// </summary>
public sealed class IngestionBatchTests
{
    private static byte[] Hash(byte fill)
    {
        byte[] h = new byte[32];
        Array.Fill(h, fill);
        return h;
    }

    [Fact]
    public void AddEntity_ReturnsHandleCarryingHashAndType()
    {
        IngestionBatch batch = new();
        byte[] hash = Hash(0xAB);
        EntityHandle h = batch.AddEntity(hash, "lemma");

        Assert.Equal("lemma", h.EntityTypeCode);
        Assert.Same(hash, h.Hash);
        Assert.Equal(1, batch.EntityCount);
    }

    [Fact]
    public void AddEdge_AcceptsHashCompositeMembersInRoleOrder()
    {
        IngestionBatch batch = new();
        EntityHandle source = batch.AddEntity(Hash(0x01), "lemma");
        EntityHandle target = batch.AddEntity(Hash(0x02), "synset");

        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(source, "source", 0),
            new EdgeMemberSpec(target, "target", 1),
        ];
        batch.AddEdge("has_sense", "princeton_wordnet", members);

        Assert.Equal(1, batch.EdgeCount);
    }

    [Fact]
    public void EntityHandle_ValueEqualityOnContent()
    {
        EntityHandle a = new(Hash(0x42), "tensor");
        EntityHandle b = new(Hash(0x42), "tensor");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

}
