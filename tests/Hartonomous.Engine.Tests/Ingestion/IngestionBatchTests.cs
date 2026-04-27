using Hartonomous.Core.Ingestion;
using Hartonomous.Engine.Ingestion;

namespace Hartonomous.Engine.Tests.Ingestion;

public sealed class IngestionBatchTests
{
    private static byte[] Hash(int seed) => Enumerable.Range(0, 32).Select(i => (byte)((seed + i) % 256)).ToArray();

    [Fact]
    public void AddEntity_ReturnsSequentialHandles()
    {
        IngestionBatch batch = new();

        EntityHandle h0 = batch.AddEntity(Hash(0), "codepoint");
        EntityHandle h1 = batch.AddEntity(Hash(1), "codepoint");
        EntityHandle h2 = batch.AddEntity(Hash(2), "collation_element");

        Assert.Equal(0, h0.BatchIndex);
        Assert.Equal(1, h1.BatchIndex);
        Assert.Equal(2, h2.BatchIndex);
    }

    [Fact]
    public void EntityCount_TracksAdditions()
    {
        IngestionBatch batch = new();
        Assert.Equal(0, batch.EntityCount);

        batch.AddEntity(Hash(0), "codepoint");
        Assert.Equal(1, batch.EntityCount);

        batch.AddEntity(Hash(1), "codepoint");
        Assert.Equal(2, batch.EntityCount);
    }

    [Fact]
    public void AddEdge_IncrementsEdgeCount()
    {
        IngestionBatch batch = new();
        EntityHandle source = batch.AddEntity(Hash(0), "codepoint");
        EntityHandle target = batch.AddEntity(Hash(1), "codepoint");

        Assert.Equal(0, batch.EdgeCount);

        batch.AddEdge("maps_to_lowercase", "unicode_consortium",
            [new EdgeMemberSpec(source, null, "source", 0),
             new EdgeMemberSpec(target, null, "target", 1)]);

        Assert.Equal(1, batch.EdgeCount);
    }

    [Fact]
    public void Entities_PreservesHashAndType()
    {
        IngestionBatch batch = new();
        byte[] hash = Hash(42);
        batch.AddEntity(hash, "codepoint");

        Assert.Single(batch.Entities);
        Assert.Equal(hash, batch.Entities[0].Hash);
        Assert.Equal("codepoint", batch.Entities[0].EntityTypeCode);
    }

    [Fact]
    public void GetEntityHash_ReturnsCorrectHash()
    {
        IngestionBatch batch = new();
        byte[] hash0 = Hash(0);
        byte[] hash1 = Hash(1);
        batch.AddEntity(hash0, "codepoint");
        batch.AddEntity(hash1, "codepoint");

        Assert.Equal(hash0, batch.GetEntityHash(0));
        Assert.Equal(hash1, batch.GetEntityHash(1));
    }

    [Fact]
    public void RemapHandle_ThenResolve_ReturnsEntityId()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        batch.RemapHandle(h.BatchIndex, 12345L);
        long resolved = batch.ResolveHandle(h);

        Assert.Equal(12345L, resolved);
    }

    [Fact]
    public void ResolveHandle_WithoutRemap_Throws()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => batch.ResolveHandle(h));
        Assert.Contains("not been remapped", ex.Message);
    }

    [Fact]
    public void ResolveHandleOrExisting_PrefersExistingId()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");
        batch.RemapHandle(h.BatchIndex, 111L);

        long resolved = batch.ResolveHandleOrExisting(h, 999L);
        Assert.Equal(999L, resolved);
    }

    [Fact]
    public void ResolveHandleOrExisting_FallsBackToHandle()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");
        batch.RemapHandle(h.BatchIndex, 111L);

        long resolved = batch.ResolveHandleOrExisting(h, null);
        Assert.Equal(111L, resolved);
    }

    [Fact]
    public void ResolveHandleOrExisting_NeitherProvided_Throws()
    {
        IngestionBatch batch = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => batch.ResolveHandleOrExisting(null, null));
        Assert.Contains("must have either", ex.Message);
    }

    [Fact]
    public void AddJunction_StoredCorrectly()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        batch.AddJunction("entity_pos", h, 42, 95000.0);

        Assert.Single(batch.Junctions);
        Assert.Equal("entity_pos", batch.Junctions[0].JunctionTable);
        Assert.Equal(h, batch.Junctions[0].Entity);
        Assert.Equal(42, batch.Junctions[0].ReferenceId);
        Assert.Equal(95000.0, batch.Junctions[0].Mu);
    }

    [Fact]
    public void AddJunction_WithoutMu_DefaultsToNull()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        batch.AddJunction("entity_pos", h, 42);

        Assert.Null(batch.Junctions[0].Mu);
    }

    [Fact]
    public void AddPhysicality_StoredCorrectly()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");
        byte[] wkb = new byte[] { 1, 2, 3, 4 };

        batch.AddPhysicality(h, "waveform", wkb);

        Assert.Single(batch.Physicalities);
        Assert.Equal(h, batch.Physicalities[0].Entity);
        Assert.Equal("waveform", batch.Physicalities[0].PhysicalityTypeCode);
        Assert.Equal(wkb, batch.Physicalities[0].Wkb);
    }

    [Fact]
    public void AddPhysicalityPoint4d_BuildsValidPostGisPointZmWkb()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        batch.AddPhysicalityPoint4d(h, "s3_position", 0.1, 0.2, 0.3, 0.4);

        Assert.Single(batch.Physicalities);
        Assert.Equal(h, batch.Physicalities[0].Entity);
        Assert.Equal("s3_position", batch.Physicalities[0].PhysicalityTypeCode);

        // POINTZM WKB: byte order (1) + type (4) + 4 doubles (32) = 37 bytes.
        byte[] wkb = batch.Physicalities[0].Wkb;
        Assert.Equal(37, wkb.Length);
        Assert.Equal(0x01, wkb[0]); // little-endian
        Assert.Equal(0xB9, wkb[1]); // type 3001 = 0x0BB9 (POINTZM)
        Assert.Equal(0x0B, wkb[2]);
    }

    [Fact]
    public void AddPhysicalityLineString4d_BuildsValidPostGisLineStringZmWkb()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "lemma");

        ReadOnlySpan<(double X1, double X2, double X3, double X4)> verts =
            new (double, double, double, double)[]
            {
                (0.1, 0.2, 0.3, 0.4),
                (0.5, 0.6, 0.7, 0.8),
            }.AsSpan();
        batch.AddPhysicalityLineString4d(h, "contour", verts);

        Assert.Single(batch.Physicalities);
        Assert.Equal(h, batch.Physicalities[0].Entity);
        Assert.Equal("contour", batch.Physicalities[0].PhysicalityTypeCode);

        // LINESTRINGZM WKB: byte order (1) + type (4) + npoints (4) + 2 * 32 = 73 bytes.
        byte[] wkb = batch.Physicalities[0].Wkb;
        Assert.Equal(73, wkb.Length);
        Assert.Equal(0x01, wkb[0]); // little-endian
        Assert.Equal(0xBA, wkb[1]); // type 3002 = 0x0BBA (LINESTRINGZM)
        Assert.Equal(0x0B, wkb[2]);
        Assert.Equal(0x02, wkb[5]); // npoints=2, low byte
    }

    [Fact]
    public void AddSequence_StoredCorrectly()
    {
        IngestionBatch batch = new();
        EntityHandle parent = batch.AddEntity(Hash(0), "sentence");
        EntityHandle child = batch.AddEntity(Hash(1), "token");

        batch.AddSequence(parent, child, 3, 1);

        Assert.Single(batch.Sequences);
        Assert.Equal(parent, batch.Sequences[0].Parent);
        Assert.Equal(child, batch.Sequences[0].Child);
        Assert.Equal(3, batch.Sequences[0].Position);
        Assert.Equal(1, batch.Sequences[0].Count);
    }

    [Fact]
    public void AddSignificance_StoredCorrectly()
    {
        IngestionBatch batch = new();
        EntityHandle h = batch.AddEntity(Hash(0), "codepoint");

        batch.AddSignificance(h, "source_authority", 95000.0);

        Assert.Single(batch.Significances);
        Assert.Equal(h, batch.Significances[0].Entity);
        Assert.Equal("source_authority", batch.Significances[0].ContextTypeCode);
        Assert.Equal(95000.0, batch.Significances[0].InitialMu);
    }

    [Fact]
    public void MultipleRemaps_EachResolvesCorrectly()
    {
        IngestionBatch batch = new();
        EntityHandle h0 = batch.AddEntity(Hash(0), "codepoint");
        EntityHandle h1 = batch.AddEntity(Hash(1), "codepoint");
        EntityHandle h2 = batch.AddEntity(Hash(2), "codepoint");

        batch.RemapHandle(h0.BatchIndex, 100L);
        batch.RemapHandle(h1.BatchIndex, 200L);
        batch.RemapHandle(h2.BatchIndex, 300L);

        Assert.Equal(100L, batch.ResolveHandle(h0));
        Assert.Equal(200L, batch.ResolveHandle(h1));
        Assert.Equal(300L, batch.ResolveHandle(h2));
    }

    [Fact]
    public void Edges_PreservesMembers()
    {
        IngestionBatch batch = new();
        EntityHandle src = batch.AddEntity(Hash(0), "codepoint");
        EntityHandle tgt = batch.AddEntity(Hash(1), "codepoint");

        EdgeMemberSpec[] members =
        [
            new(src, null, "source", 0),
            new(tgt, null, "target", 1),
        ];
        batch.AddEdge("maps_to_lowercase", "unicode_consortium", members);

        Assert.Single(batch.Edges);
        Assert.Equal("maps_to_lowercase", batch.Edges[0].EdgeTypeCode);
        Assert.Equal("unicode_consortium", batch.Edges[0].ProvenanceCode);
        Assert.Equal(2, batch.Edges[0].Members.Length);
        Assert.Equal("source", batch.Edges[0].Members[0].RoleCode);
        Assert.Equal("target", batch.Edges[0].Members[1].RoleCode);
    }

    [Fact]
    public void AddEdge_WithExistingEntityId_PreservesIt()
    {
        IngestionBatch batch = new();
        EntityHandle src = batch.AddEntity(Hash(0), "codepoint");

        EdgeMemberSpec[] members =
        [
            new(src, null, "source", 0),
            new(null, 99999L, "target", 1),
        ];
        batch.AddEdge("has_collation_weight", "unicode_consortium", members);

        Assert.Equal(99999L, batch.Edges[0].Members[1].ExistingEntityId);
        Assert.Null(batch.Edges[0].Members[1].Handle);
    }
}
