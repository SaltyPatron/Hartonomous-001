using System;

namespace Hartonomous.Core.Ingestion;

public interface IIngestionBatch
{
    EntityHandle AddEntity(byte[] hash, string entityTypeCode);

    void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members);

    void AddJunction(
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu = null);

    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geomWkb);

    void AddSequence(
        EntityHandle parent,
        EntityHandle child,
        int position,
        int count = 1);

    void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu);

    int EntityCount { get; }

    int EdgeCount { get; }
}
