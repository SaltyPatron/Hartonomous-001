using System;
using System.Collections.Generic;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// In-memory accumulator for one ingestion transaction. Pure value carrier:
/// every Add* call captures the spec; the pipeline reads the typed lists at
/// flush and emits COPY-stream rows. There is no RemapHandle, no
/// ResolveHandle, no _handleToEntityId dictionary in the hash-as-PK
/// substrate — handles are foreign keys, written directly.
/// </summary>
internal sealed class IngestionBatch : IIngestionBatch
{
    private readonly List<EntityEntry> _entities = [];
    private readonly List<EdgeEntry> _edges = [];
    private readonly List<JunctionEntry> _junctions = [];
    private readonly List<PhysicalityEntry> _physicalities = [];
    private readonly List<SequenceEntry> _sequences = [];
    private readonly List<SignificanceEntry> _significances = [];
    private readonly List<EntityModelSourceEntry> _entityModelSources = [];

    public IngestionBatch(string provenanceCode)
    {
        ProvenanceCode = provenanceCode;
    }

    public string ProvenanceCode { get; }

    public int EntityCount => _entities.Count;
    public int EdgeCount => _edges.Count;

    public IReadOnlyList<EntityEntry> Entities => _entities;
    public IReadOnlyList<EdgeEntry> Edges => _edges;
    public IReadOnlyList<JunctionEntry> Junctions => _junctions;
    public IReadOnlyList<PhysicalityEntry> Physicalities => _physicalities;
    public IReadOnlyList<SequenceEntry> Sequences => _sequences;
    public IReadOnlyList<SignificanceEntry> Significances => _significances;
    public IReadOnlyList<EntityModelSourceEntry> EntityModelSources => _entityModelSources;

    public EntityHandle AddEntity(byte[] hash, string entityTypeCode)
    {
        _entities.Add(new EntityEntry(hash, entityTypeCode));
        return new EntityHandle(hash, entityTypeCode);
    }

    public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members)
    {
        _edges.Add(new EdgeEntry(
            edgeTypeCode, provenanceCode, members.ToArray(),
            System.Array.Empty<EdgeSignificanceSpec>(),
            System.Array.Empty<EdgeRatingEvent>()));
    }

    public void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members,
        ReadOnlySpan<EdgeSignificanceSpec> significance)
    {
        _edges.Add(new EdgeEntry(
            edgeTypeCode, provenanceCode, members.ToArray(),
            significance.ToArray(),
            System.Array.Empty<EdgeRatingEvent>()));
    }

    public void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members,
        ReadOnlySpan<EdgeSignificanceSpec> significance,
        ReadOnlySpan<EdgeRatingEvent> events)
    {
        _edges.Add(new EdgeEntry(
            edgeTypeCode, provenanceCode, members.ToArray(),
            significance.ToArray(),
            events.ToArray()));
    }

    public void AddJunction(
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu = null,
        string attestationTypeCode = "lexical_curated_relation")
    {
        _junctions.Add(new JunctionEntry(junctionTable, entity, referenceId, mu, attestationTypeCode));
    }

    public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
    {
        // Generic-WKB path: extract centroid from the buffer. The typed
        // helpers below are preferred because they already know the centroid
        // and skip the parse.
        if (!Hartonomous.Core.Geometry.PostGisWkbReader.TryExtractCentroid(geomWkb, out Point4D centroid))
        {
            throw new ArgumentException(
                $"AddPhysicality: could not extract a 4D centroid from the supplied WKB " +
                $"(entity {Convert.ToHexString(entity.Hash)}, type {physicalityTypeCode}, " +
                $"{geomWkb.Length} bytes). Either the WKB subtype is unsupported by " +
                $"PostGisWkbReader or the buffer is malformed. Use AddPhysicalityPoint4d " +
                $"or AddPhysicalityLineString4d when the centroid is already known.",
                nameof(geomWkb));
        }
        _physicalities.Add(new PhysicalityEntry(entity, physicalityTypeCode, geomWkb, centroid));
    }

    public void AddPhysicalityPoint4d(
        EntityHandle entity,
        string physicalityTypeCode,
        double x1, double x2, double x3, double x4)
    {
        Point4D pt = new(x1, x2, x3, x4);
        _physicalities.Add(new PhysicalityEntry(
            entity,
            physicalityTypeCode,
            PostGisWkbBuilder.PointZM(pt),
            pt));
    }

    public void AddPhysicalityLineString4d(
        EntityHandle entity,
        string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException(
                "LINESTRINGZM requires at least one vertex.", nameof(vertices));
        }
        // Promote the tuple-based decomposer surface to Point4D once; from
        // here down everything is Point4D.
        Span<Point4D> typed = vertices.Length <= 64
            ? stackalloc Point4D[vertices.Length]
            : new Point4D[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            typed[i] = new Point4D(vertices[i].X1, vertices[i].X2, vertices[i].X3, vertices[i].X4);
        }

        Point4D centroid;
        if (!Point4D.TryMean(typed, out centroid))
        {
            throw new ArgumentException(
                "AddPhysicalityLineString4d: vertex span yielded no centroid.",
                nameof(vertices));
        }

        _physicalities.Add(new PhysicalityEntry(
            entity,
            physicalityTypeCode,
            PostGisWkbBuilder.LineStringZM((ReadOnlySpan<Point4D>)typed),
            centroid));
    }

    public void AddSequence(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
    {
        _sequences.Add(new SequenceEntry(parent, ordinal, child, rleCount));
    }

    public void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu,
        string attestationTypeCode = "provenance_authority_corroboration")
    {
        _significances.Add(new SignificanceEntry(entity, contextTypeCode, initialMu, attestationTypeCode));
    }

    public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
    {
        _entityModelSources.Add(new EntityModelSourceEntry(entity, modelSourceId));
    }
}
