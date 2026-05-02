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
        IngestionBatch batch = new("test");
        byte[] hash = Hash(0xAB);
        EntityHandle h = batch.AddEntity(hash, "lemma");

        Assert.Equal("lemma", h.EntityTypeCode);
        Assert.Same(hash, h.Hash);
        Assert.Equal(1, batch.EntityCount);
    }

    [Fact]
    public void Constructor_carries_provenance_for_the_batch()
    {
        // Per-batch provenance is the contract: every classification +
        // edge in this batch attributes to this provenance, no batch-level
        // fallback to "system_computed". Regression for the bug where
        // SubmitBatchAsync derived provenance from edges (and fell back to
        // system_computed for edge-less batches), masking which decomposer
        // asserted what.
        IngestionBatch tatoeba = new("tatoeba");
        IngestionBatch wordnet = new("princeton_wordnet");

        Assert.Equal("tatoeba", tatoeba.ProvenanceCode);
        Assert.Equal("princeton_wordnet", wordnet.ProvenanceCode);

        // Provenance is set at construction and stable for the batch's
        // lifetime — never fallbacks, never derived.
        tatoeba.AddEntity(Hash(0x01), "tatoeba_sentence");
        Assert.Equal("tatoeba", tatoeba.ProvenanceCode);
    }

    [Fact]
    public void Different_batches_carry_independent_provenance()
    {
        // Two batches running in parallel must not cross-contaminate.
        IngestionBatch a = new("decomposer_a");
        IngestionBatch b = new("decomposer_b");

        a.AddEntity(Hash(0x01), "lemma");
        b.AddEntity(Hash(0x02), "synset");

        Assert.Equal("decomposer_a", a.ProvenanceCode);
        Assert.Equal("decomposer_b", b.ProvenanceCode);
    }

    [Fact]
    public void AddEdge_AcceptsHashCompositeMembersInRoleOrder()
    {
        IngestionBatch batch = new("test");
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
