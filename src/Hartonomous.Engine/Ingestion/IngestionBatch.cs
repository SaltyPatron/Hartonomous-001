using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;
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
    private readonly List<CompositionChildEntry> _compositionChildren = [];
    // Dedup key must match the BuildCompositionMetadata invariant
    // (one child per (parent, ordinal) slot). Including ChildHash/RleCount
    // in the key would let two emissions claim the same slot with different
    // children — the metadata builder then throws "overlaps at ordinal N".
    // Per substrate spec: composition's LINESTRINGZM has one child vertex
    // per ordinal; sequence semantics, not multi-set. First emission wins
    // by content-addressed identity; subsequent conflicting emissions are
    // spurious re-emission of the same parent's manifest from a different
    // code path within the same batch.
    private readonly HashSet<(Hash32 ParentHash, int Ordinal)> _compositionChildKeys = [];
    private long _compositionChildConflicts;
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
    public IReadOnlyList<CompositionChildEntry> CompositionChildren => _compositionChildren;
    public IReadOnlyList<SignificanceEntry> Significances => _significances;
    public IReadOnlyList<EntityModelSourceEntry> EntityModelSources => _entityModelSources;

    public EntityHandle AddEntity(Hash32 hash, string entityTypeCode)
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
        string attestationTypeCode = "positive_evidence")
    {
        _junctions.Add(new JunctionEntry(junctionTable, entity, referenceId, mu, attestationTypeCode));
    }

    public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geometryPayload)
    {
        if (!Geometry4dPayloadBuilder.TryExtractCentroid(geometryPayload, out Point4D centroid))
        {
            throw new ArgumentException(
                $"AddPhysicality: could not extract a 4D centroid from the supplied geometry4d payload " +
                $"(entity {entity.Hash.ToHexString()}, type {physicalityTypeCode}, " +
                $"{geometryPayload.Length} bytes). Use AddPhysicalityPoint4d " +
                $"or AddPhysicalityLineString4d when the centroid is already known.",
                nameof(geometryPayload));
        }
        _physicalities.Add(new PhysicalityEntry(entity, physicalityTypeCode, geometryPayload, centroid));
    }

    public void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geometryPayload,
        Point4D centroid)
    {
        _physicalities.Add(new PhysicalityEntry(entity, physicalityTypeCode, geometryPayload, centroid));
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
            Geometry4dPayloadBuilder.Point(pt),
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
                "LINESTRING4D requires at least one vertex.", nameof(vertices));
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
            Geometry4dPayloadBuilder.LineString((ReadOnlySpan<Point4D>)typed),
            centroid));
    }

    public void AddCompositionChild(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
    {
        if (!_compositionChildKeys.Add((parent.Hash, ordinal)))
        {
            // Conflicting re-emission of the same (parent, ordinal) slot.
            // Composition metadata invariant requires exactly one child per
            // ordinal — first emission wins, subsequent emissions dropped.
            // Counter surfaces to the pipeline for triage visibility.
            System.Threading.Interlocked.Increment(ref _compositionChildConflicts);
            return;
        }
        _compositionChildren.Add(new CompositionChildEntry(parent, ordinal, child, rleCount));
    }

    public long CompositionChildConflicts => System.Threading.Interlocked.Read(ref _compositionChildConflicts);

    public void AddEntityShape(EntityHandle entity, ReadOnlySpan<Point4D> canonicalChildCentroids)
    {
        if (canonicalChildCentroids.Length < 1)
        {
            throw new ArgumentException(
                "AddEntityShape requires at least one centroid.",
                nameof(canonicalChildCentroids));
        }

        byte[] geometry;
        Point4D centroid;
        if (canonicalChildCentroids.Length == 1)
        {
            centroid = canonicalChildCentroids[0];
            geometry = Geometry4dPayloadBuilder.Point(centroid);
        }
        else
        {
            if (!Point4D.TryMean(canonicalChildCentroids, out centroid))
            {
                throw new ArgumentException(
                    "AddEntityShape: vertex span yielded no centroid.",
                    nameof(canonicalChildCentroids));
            }
            geometry = Geometry4dPayloadBuilder.LineString(canonicalChildCentroids);
        }

        _physicalities.Add(new PhysicalityEntry(
            entity,
            "entity",
            geometry,
            centroid));
    }

    public void AddIngestionTrajectory(EntityHandle parent, ReadOnlySpan<TrajectoryVertex> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException(
                "AddIngestionTrajectory requires at least one vertex.",
                nameof(vertices));
        }

        Span<Point4D> packed = vertices.Length <= 64
            ? stackalloc Point4D[vertices.Length]
            : new Point4D[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            TrajectoryVertex v = vertices[i];
            packed[i] = new Point4D(
                MantissaPacking.PackHashLo(v.ChildHashLo),
                MantissaPacking.PackOrdinalRle(v.Ordinal, v.Rle),
                MantissaPacking.PackHashHi(v.ChildHashHi),
                MantissaPacking.PackMetadata(v.Metadata));
        }

        // Mean of packed vertices serves as the entity's centroid for
        // downstream edge.geom inline construction. The value is not metric
        // — it's the mean of mantissa-packed identity bits — but it is
        // deterministic and consistent across runs, so edges constructed
        // through this entity's centroid land in the same structural-
        // identity coordinate space as the content trajectory itself.
        // GiST bbox queries against the content partition operate on the
        // same coordinate space.
        if (!Point4D.TryMean((ReadOnlySpan<Point4D>)packed, out Point4D centroid))
        {
            throw new ArgumentException(
                "AddIngestionTrajectory: vertex span yielded no centroid.",
                nameof(vertices));
        }

        byte[] geometry = Geometry4dPayloadBuilder.LineString((ReadOnlySpan<Point4D>)packed);

        _physicalities.Add(new PhysicalityEntry(
            parent,
            "content",
            geometry,
            centroid));
    }

    public void AddFireflyPoint(EntityHandle parent, long modelSourceId, Point4D projection)
    {
        // Firefly POINTZM per (entity, ingested_model). Content-addressed
        // via the projection bytes; per-model differentiation rides on the
        // entity_model_source link added below. Two different models that
        // happen to project an identical (x, y, z, m) tuple for the same
        // word_form collide on content_hash by design — that's the rare
        // case where two ingested models agree exactly on a token's 4D
        // identity, which the substrate should not duplicate-record.
        // Procrustes alignment per AP-35 keeps per-model bases
        // commensurable so projections aren't accidentally identical.
        byte[] geometry = Geometry4dPayloadBuilder.Point(projection);
        _physicalities.Add(new PhysicalityEntry(
            parent,
            "firefly",
            geometry,
            projection));
        _entityModelSources.Add(new EntityModelSourceEntry(parent, modelSourceId));
    }

    public void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu,
        string attestationTypeCode = "positive_evidence")
    {
        _significances.Add(new SignificanceEntry(entity, contextTypeCode, initialMu, attestationTypeCode));
    }

    public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
    {
        _entityModelSources.Add(new EntityModelSourceEntry(entity, modelSourceId));
    }
}
