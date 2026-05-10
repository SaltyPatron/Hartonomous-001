using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Decomposers.Safetensors.Passes;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Records every emission a pass makes for assertion in unit tests. No DB,
/// no pipeline — purely captures the call sequence.
/// </summary>
internal sealed class RecordingPassSession : IPassSession
{
    public RecordingBatch Batch { get; } = new();
    IIngestionBatch IPassSession.Batch => Batch;
    public EntityHandle ModelEntity => new(new byte[32], "model_architecture");
    public long EntitiesCreated => Batch.Entities.Count;
    public long EdgesCreated => Batch.Edges.Count;
    public Task MaybeFlushAsync(int threshold, CancellationToken ct) => Task.CompletedTask;
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class RecordingBatch : IIngestionBatch
{
    public string ProvenanceCode { get; set; } = "test_provenance";

    public List<(byte[] Hash, string TypeCode)> Entities { get; } = [];
    public List<RecordedEdge> Edges { get; } = [];
    public List<(string Junction, EntityHandle Entity, int RefId, double? Mu, string AttestationType)> Junctions { get; } = [];
    public List<(EntityHandle Entity, string PhysType, byte[] Wkb)> Physicalities { get; } = [];
    public List<(EntityHandle Entity, string PhysType, double X, double Y, double Z, double M)> PhysPoints { get; } = [];
    public List<(EntityHandle Entity, string PhysType, (double X, double Y, double Z, double M)[] Vertices)> PhysLines { get; } = [];
    public List<(EntityHandle Parent, int Ordinal, EntityHandle Child, int RleCount)> Sequences { get; } = [];
    public List<(EntityHandle Entity, string Arena, double Mu, string AttestationType)> Significances { get; } = [];
    public List<(EntityHandle Entity, long ModelSourceId)> ModelSources { get; } = [];

    public EntityHandle AddEntity(byte[] hash, string entityTypeCode)
    {
        Entities.Add((hash, entityTypeCode));
        return new EntityHandle(hash, entityTypeCode);
    }

    public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members)
    {
        Edges.Add(new RecordedEdge(edgeTypeCode, provenanceCode, members.ToArray(), Array.Empty<EdgeSignificanceSpec>()));
    }

    public void AddEdge(string edgeTypeCode, string provenanceCode, ReadOnlySpan<EdgeMemberSpec> members, ReadOnlySpan<EdgeSignificanceSpec> significance)
    {
        Edges.Add(new RecordedEdge(edgeTypeCode, provenanceCode, members.ToArray(), significance.ToArray()));
    }

    public void AddJunction(string junctionTable, EntityHandle entity, int referenceId, double? mu = null, string attestationTypeCode = "lexical_curated_relation")
    {
        Junctions.Add((junctionTable, entity, referenceId, mu, attestationTypeCode));
    }

    public void AddPhysicality(EntityHandle entity, string physicalityTypeCode, byte[] geomWkb)
    {
        Physicalities.Add((entity, physicalityTypeCode, geomWkb));
    }

    public void AddPhysicalityPoint4d(EntityHandle entity, string physicalityTypeCode, double x1, double x2, double x3, double x4)
    {
        PhysPoints.Add((entity, physicalityTypeCode, x1, x2, x3, x4));
    }

    public void AddPhysicalityLineString4d(
        EntityHandle entity, string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices)
    {
        (double X, double Y, double Z, double M)[] copy = new (double, double, double, double)[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            copy[i] = (vertices[i].X1, vertices[i].X2, vertices[i].X3, vertices[i].X4);
        }
        PhysLines.Add((entity, physicalityTypeCode, copy));
    }

    public void AddSequence(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
    {
        Sequences.Add((parent, ordinal, child, rleCount));
    }

    public void AddSignificance(EntityHandle entity, string contextTypeCode, double initialMu, string attestationTypeCode = "provenance_authority_corroboration")
    {
        Significances.Add((entity, contextTypeCode, initialMu, attestationTypeCode));
    }

    public void AddEntityModelSource(EntityHandle entity, long modelSourceId)
    {
        ModelSources.Add((entity, modelSourceId));
    }

    public int EntityCount => Entities.Count;
    public int EdgeCount => Edges.Count;
}

internal sealed record RecordedEdge(
    string EdgeTypeCode,
    string ProvenanceCode,
    EdgeMemberSpec[] Members,
    EdgeSignificanceSpec[] Significance);
