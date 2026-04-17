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

    /// <summary>
    /// Record that <paramref name="entity"/> was observed in the given model_source.
    /// Per-model identity (registry, publisher, slug, revision) is captured in the
    /// model_source row; this method links the entity to that row via the
    /// substrate.entity_model_source junction. Same entity hash appearing in N models =
    /// one entity, N junction rows.
    /// </summary>
    void AddEntityModelSource(EntityHandle entity, long modelSourceId);

    int EntityCount { get; }

    int EdgeCount { get; }
}
