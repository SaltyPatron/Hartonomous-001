using System;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Producer-side surface that decomposers stream tuples into. Pure values:
/// hashes + reference codes + geometry payloads. The pipeline owns batching,
/// transactions, partitioning, and ordering. There is no ResolveHandle and
/// no RemapHandle in the hash-as-PK substrate — handles ARE the foreign keys.
/// </summary>
public interface IIngestionBatch
{
    /// <summary>
    /// Each batch carries a single provenance — the decomposer asserting
    /// these facts. Entity classifications and edges all attribute to this
    /// provenance unless overridden per-emission. Pipeline reads this when
    /// flushing entity_classification and edge rows. Decomposers set this
    /// once at batch creation; the pipeline derives provenance from it
    /// rather than fishing through edges.
    /// </summary>
    string ProvenanceCode { get; }

    /// <summary>
    /// Append an entity. Returns a handle that carries the hash + type code;
    /// downstream Add* calls reference this handle to express FKs. Same
    /// (hash, type_code) added twice is idempotent at flush via
    /// ON CONFLICT DO NOTHING on substrate.entity's composite PK.
    /// </summary>
    EntityHandle AddEntity(byte[] hash, string entityTypeCode);

    /// <summary>
    /// Append an n-ary edge. The pipeline computes the edge hash from
    /// (edge_type_id, ordered participant hashes) at flush. Each member
    /// references its participating entity by handle.
    /// </summary>
    void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members);

    /// <summary>
    /// Append a junction row (entity_pos, entity_sense, entity_language,
    /// entity_morph_feature, codepoint_property, model_architecture_class,
    /// tensor_tensor_role, pattern_deprel). Junction tables FK on
    /// (entity_type_id, entity_hash) directly.
    /// </summary>
    void AddJunction(
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu = null);

    /// <summary>
    /// Append a physicality row with raw PostGIS WKB. Used for 2D/3D
    /// audio physicality types whose vertex layout doesn't fit POINTZM /
    /// LINESTRINGZM (waveform, FFT, STFT, MFCC, chromagram, formant
    /// trajectory, etc.). The pipeline routes the WKB into the geom column
    /// via ST_GeomFromWKB.
    /// </summary>
    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geomWkb);

    /// <summary>
    /// Append a 4D POINTZM physicality row (s3_position, hilbert_value,
    /// weight_distribution single-point variants, etc.).
    /// </summary>
    void AddPhysicalityPoint4d(
        EntityHandle entity,
        string physicalityTypeCode,
        double x1,
        double x2,
        double x3,
        double x4);

    /// <summary>
    /// Append a 4D LINESTRINGZM physicality row (contour, weight_distribution
    /// trajectory variants, etc.). Vertices in trajectory order; at least one
    /// vertex required.
    /// </summary>
    void AddPhysicalityLineString4d(
        EntityHandle entity,
        string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices);

    /// <summary>
    /// Append a sequence row recording that <paramref name="parent"/> contains
    /// <paramref name="child"/> at <paramref name="ordinal"/> (1-based).
    /// <paramref name="rleCount"/> compresses contiguous runs of the same
    /// child — a refrain of three identical sentences in a row collapses to
    /// one sequence row with rleCount=3, lookup at any ordinal in the run
    /// still hits the row via <c>ordinal &lt;= N AND ordinal + rle_count &gt; N</c>.
    ///
    /// This is THE record of "where does X sit inside Y?". The parent's
    /// LINESTRINGZM physicality remains the geometric truth for similarity
    /// queries; substrate.sequence is the indexed identity-and-ordinal record
    /// that powers microsecond random access by position.
    /// </summary>
    void AddSequence(
        EntityHandle parent,
        int ordinal,
        EntityHandle child,
        int rleCount = 1);

    /// <summary>
    /// Append an entity-significance prime row in the given arena.
    /// Edge significance is primed in bulk by a separate substrate
    /// procedure, not per-batch.
    /// </summary>
    void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu);

    /// <summary>
    /// Record that <paramref name="entity"/> was observed in the given
    /// model_source. Same entity hash appearing in N model sources = one
    /// substrate.entity row, N substrate.entity_model_source rows.
    /// </summary>
    void AddEntityModelSource(EntityHandle entity, long modelSourceId);

    int EntityCount { get; }
    int EdgeCount { get; }
}
