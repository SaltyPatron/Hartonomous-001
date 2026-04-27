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

    /// <summary>
    /// Append a PostGIS-typed physicality row. Only valid for physicality types
    /// whose <c>physicality_type.dimensionality</c> is 2 or 3 (waveform, FFT,
    /// STFT, MFCC, chromagram, formant trajectory, SVD spectrum, etc.). The
    /// pipeline routes the WKB into the <c>geom</c> column via
    /// <c>ST_GeomFromWKB(g, 4326)</c>.
    /// </summary>
    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geomWkb);

    /// <summary>
    /// Append a substrate-native 4D point physicality row. Used for every type
    /// whose <c>physicality_type.dimensionality</c> is 4 and whose realization
    /// is a single point: <c>s3_position</c>, <c>hilbert_value</c>,
    /// <c>weight_distribution</c>, <c>embedding_firefly</c>,
    /// <c>codec_codevector_position</c>. The pipeline routes the four
    /// coordinates into the <c>geom geometry(GeometryZM)</c> column via
    /// <c>ST_MakePoint(x, y, z, m)</c> (post-migration 0048).
    /// </summary>
    void AddPhysicalityPoint4d(
        EntityHandle entity,
        string physicalityTypeCode,
        double x1,
        double x2,
        double x3,
        double x4);

    /// <summary>
    /// Append a substrate-native 4D polyline physicality row. Used for every
    /// 4D trajectory physicality (currently <c>contour</c>). The pipeline
    /// routes the vertices into the <c>geom geometry(GeometryZM)</c> column
    /// as a LINESTRINGZM via PostGIS native constructors (post-migration 0048).
    /// </summary>
    /// <param name="vertices">Vertices in trajectory order. Must contain at
    /// least one vertex; each vertex is a 4-tuple (x1, x2, x3, x4).</param>
    void AddPhysicalityLineString4d(
        EntityHandle entity,
        string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices);

    void AddSequence(
        EntityHandle parent,
        EntityHandle child,
        int position,
        int count = 1);

    /// <summary>
    /// Same as <see cref="AddSequence(EntityHandle, EntityHandle, int, int)"/>
    /// but takes the parent's already-resolved <c>substrate.entity.id</c>
    /// directly instead of a per-batch <see cref="EntityHandle"/>. Use this
    /// when the parent is known via stable substrate id (e.g. a TensorHandle's
    /// EntityId in safetensors decomposition) so the sequence row is not at
    /// the mercy of cross-batch handle remapping. Fixes the silent miswrite
    /// where flush invalidated the tensor handle and subsequent sequence
    /// rows pointed at the WRONG entity (e.g. 4998 of 30522 embedding rows
    /// linked to the tensor; the rest pointed at random embedding_position
    /// entities promoted by the post-flush handle reuse).
    /// </summary>
    void AddSequence(
        long parentEntityId,
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

