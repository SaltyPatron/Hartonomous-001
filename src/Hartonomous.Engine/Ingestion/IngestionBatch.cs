using System;
using System.Collections.Generic;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal sealed class IngestionBatch : IIngestionBatch
{
    private readonly List<EntityEntry> _entities = [];
    private readonly List<EdgeEntry> _edges = [];
    private readonly List<JunctionEntry> _junctions = [];
    private readonly List<PhysicalityEntry> _physicalities = [];
    private readonly List<SequenceEntry> _sequences = [];
    private readonly List<SignificanceEntry> _significances = [];
    private readonly List<EntityModelSourceEntry> _entityModelSources = [];
    private readonly Dictionary<int, long> _handleToEntityId = [];

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
        int index = _entities.Count;
        _entities.Add(new EntityEntry(hash, entityTypeCode));
        return new EntityHandle(index);
    }

    public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members)
    {
        _edges.Add(new EdgeEntry(edgeTypeCode, provenanceCode, members.ToArray()));
    }

    public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null)
    {
        _junctions.Add(new JunctionEntry(junctionTable, entity, referenceId, mu));
    }

    public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
    {
        _physicalities.Add(new PhysicalityEntry(
            entity,
            physicalityTypeCode,
            PhysicalitySurface.PostGisGeom,
            geomWkb,
            null,
            null));
    }

    public void AddPhysicalityPoint4d(
        EntityHandle entity,
        string physicalityTypeCode,
        double x1,
        double x2,
        double x3,
        double x4)
    {
        _physicalities.Add(new PhysicalityEntry(
            entity,
            physicalityTypeCode,
            PhysicalitySurface.Point4D,
            null,
            [x1, x2, x3, x4],
            null));
    }

    public void AddPhysicalityLineString4d(
        EntityHandle entity,
        string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices)
    {
        if (vertices.Length < 1)
        {
            throw new ArgumentException(
                "linestring4d requires at least one vertex.", nameof(vertices));
        }
        double[] flat = new double[vertices.Length * 4];
        for (int i = 0; i < vertices.Length; i++)
        {
            flat[i * 4 + 0] = vertices[i].X1;
            flat[i * 4 + 1] = vertices[i].X2;
            flat[i * 4 + 2] = vertices[i].X3;
            flat[i * 4 + 3] = vertices[i].X4;
        }
        _physicalities.Add(new PhysicalityEntry(
            entity,
            physicalityTypeCode,
            PhysicalitySurface.LineString4D,
            null,
            null,
            flat));
    }

    public void AddSequence(EntityHandle parent, EntityHandle child, int position, int count = 1)
    {
        _sequences.Add(new SequenceEntry(parent, child, position, count));
    }

    public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu)
    {
        _significances.Add(new SignificanceEntry(entity, contextTypeCode, initialMu));
    }

    public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
    {
        _entityModelSources.Add(new EntityModelSourceEntry(entity, modelSourceId));
    }

    public void RemapHandle(int batchIndex, long entityId)
    {
        _handleToEntityId[batchIndex] = entityId;
    }

    public long ResolveHandle(EntityHandle handle)
    {
        if (_handleToEntityId.TryGetValue(handle.BatchIndex, out long id))
        {
            return id;
        }
        throw new InvalidOperationException($"EntityHandle {handle.BatchIndex} has not been remapped to a real entity ID.");
    }

    public long ResolveHandleOrExisting(EntityHandle? handle, long? existingId)
    {
        if (existingId.HasValue)
        {
            return existingId.Value;
        }
        if (handle.HasValue)
        {
            return ResolveHandle(handle.Value);
        }
        throw new InvalidOperationException("Edge member must have either a Handle or an ExistingEntityId.");
    }

    public byte[] GetEntityHash(int batchIndex)
    {
        return _entities[batchIndex].Hash;
    }
}
