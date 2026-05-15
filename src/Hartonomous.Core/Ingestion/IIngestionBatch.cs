using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Geometry;

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
    /// hash added twice is idempotent at flush via ON CONFLICT DO NOTHING
    /// on substrate.entity's hash-only PK; type code is emitted separately
    /// as entity_classification evidence.
    /// </summary>
    EntityHandle AddEntity(Hash32 hash, string entityTypeCode);

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
    /// Append an n-ary edge plus producer-calibrated initial Glicko-2 mu
    /// values for one or more arenas. Used when the decomposer can derive
    /// a meaningful prior from the source data — e.g. FfnEdgeDecompositionPass
    /// scales mu by the signed weight relative to the tensor's mean magnitude
    /// for the <c>model_trust</c> arena. Arenas not covered by
    /// <paramref name="significance"/> receive the provenance default.
    ///
    /// Default implementation drops the significance specs and falls back to
    /// the 3-arg overload for test doubles and producer surfaces that do not
    /// model producer-calibrated priors; the pipeline implementation overrides
    /// this to honor the overrides.
    /// </summary>
    void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members,
        ReadOnlySpan<EdgeSignificanceSpec> significance)
        => AddEdge(edgeTypeCode, provenanceCode, members);

    /// <summary>
    /// Append an n-ary edge plus sign-bearing Glicko-2 rating events. Each
    /// event in <paramref name="events"/> fires
    /// <c>substrate.record_attestation</c> on the resulting edge under
    /// (event.ContextTypeCode, event.AttestationTypeCode), with score
    /// encoding sign (1 = positive evidence, 0 = negative) and weight
    /// scaling the per-event Glicko period.
    ///
    /// Distinct from the spec-only overload: spec primes default mu on
    /// insert; event fires Glicko on every emission, so cross-model
    /// corroboration accumulates on the same (arena, edge, attestation_type)
    /// row instead of being ON-CONFLICT-DO-NOTHING'd into silence.
    ///
    /// Per docs/01-tensor-primitive-spec.md §V and AP-31. Decomposers that
    /// extract sign-bearing measurements from tensor weights MUST use this
    /// overload — sign-throwing is the spec's primary banned anti-pattern
    /// for tensor decomposition.
    ///
    /// Default implementation falls back to the 3-arg overload — keeps test
    /// fakes compatible without forcing event modeling per-test.
    /// </summary>
    void AddEdge(
        string edgeTypeCode,
        string provenanceCode,
        ReadOnlySpan<EdgeMemberSpec> members,
        ReadOnlySpan<EdgeSignificanceSpec> significance,
        ReadOnlySpan<EdgeRatingEvent> events)
        => AddEdge(edgeTypeCode, provenanceCode, members, significance);

    /// <summary>
    /// Append a junction row (entity_pos, entity_language,
    /// entity_morph_feature, codepoint_property, model_architecture_class,
    /// entity_lexname, tensor_tensor_role, pattern_deprel). Junction tables
    /// FK to substrate.entity(hash) through entity_hash.
    ///
    /// AttestationTypeCode stratifies Glicko-bearing junctions (entity_pos,
    /// pattern_deprel) per kind of evidence. Default lexical_curated_relation
    /// matches the dominant ingestion path (POS/deprel curators); model
    /// decomposers should pass model_attention_pattern or similar.
    /// Non-Glicko junctions ignore the value at the drain boundary.
    /// </summary>
    void AddJunction(
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu = null,
        string attestationTypeCode = "lexical_curated_relation");

    /// <summary>
    /// Append a physicality row with a native geometry4d payload.
    /// </summary>
    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geometryPayload);

    void AddPhysicality(
        EntityHandle entity,
        string physicalityTypeCode,
        byte[] geometryPayload,
        Point4D centroid)
        => AddPhysicality(entity, physicalityTypeCode, geometryPayload);

    /// <summary>
    /// Append a POINT4D physicality row (s3_position, hilbert_value,
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
    /// Append a LINESTRING4D physicality row (contour, weight_distribution
    /// trajectory variants, etc.). Vertices in trajectory order; at least one
    /// vertex required.
    /// </summary>
    void AddPhysicalityLineString4d(
        EntityHandle entity,
        string physicalityTypeCode,
        ReadOnlySpan<(double X1, double X2, double X3, double X4)> vertices);

    /// <summary>
    /// Append composition child metadata for the parent's physicality-backed
    /// trajectory. Ordinal is 1-based; <paramref name="rleCount"/> compresses
    /// contiguous runs of the same child.
    ///
    /// <para>
    /// Deprecated. The trajectory model replaces composition-child rows with
    /// <see cref="AddIngestionTrajectory"/> emitting a mantissa-packed
    /// LINESTRING4D (or <see cref="AddIngestionMultiTrajectory"/> for
    /// multi-segment / multi-tier compositions). The two-trajectory model
    /// keeps canonical entity shape (<see cref="AddEntityShape"/>) and
    /// recorded ingestion content (<see cref="AddIngestionTrajectory"/>) on
    /// separate <c>physicality_type</c> rows. Scheduled for deletion in S5.
    /// </para>
    /// </summary>
    void AddCompositionChild(
        EntityHandle parent,
        int ordinal,
        EntityHandle child,
        int rleCount = 1);

    /// <summary>
    /// Emit the entity's <b>canonical shape</b> physicality — real-coordinate
    /// LINESTRINGZM through the children's centroids in canonical order.
    /// Used for shape lookup / fingerprint matching via
    /// <c>substrate.st_4d_frechet_distance</c>: same entity, same shape,
    /// regardless of which decomposition encountered it.
    ///
    /// <para>
    /// For atoms (codepoints), call <see cref="AddPhysicalityPoint4d"/> with
    /// <c>physicalityTypeCode = "entity"</c> instead — atoms have a POINTZM
    /// shape, not a linestring. For compositions, this emits a row with
    /// <c>physicality_type = entity_shape</c> and <c>geom = LINESTRINGZM</c>
    /// in real metric coordinates (NOT bit-banged identity).
    /// </para>
    ///
    /// <para>
    /// At least one centroid required. Same children sequence on the same
    /// entity ⇒ same row by content-addressed dedup at the physicality PK.
    /// </para>
    /// </summary>
    void AddEntityShape(
        EntityHandle entity,
        ReadOnlySpan<Point4D> canonicalChildCentroids)
        => throw new NotSupportedException(
            "AddEntityShape is wired by the pipeline's IIngestionBatch implementation in S3. Test doubles can override.");

    /// <summary>
    /// Emit the composition's <b>recorded ingestion trajectory</b> as a
    /// mantissa-packed LINESTRINGZM — one vertex per child in trajectory
    /// order, X+Z carrying the child's 104-bit hash prefix, Y carrying
    /// ordinal+RLE, M carrying free-form metadata. Used for bit-perfect
    /// reconstruction via <c>SubstrateTierWalker.WalkAsync</c>.
    ///
    /// <para>
    /// Emits a row with <c>physicality_type = ingestion_trajectory</c> and
    /// <c>geom = LINESTRINGZM</c>. Children are recovered via the
    /// <c>(hash_bits_0_51, hash_bits_52_103)</c> composite btree on
    /// <c>substrate.entity</c> — one batched lookup per tier walk, no
    /// GiST reverse-spatial query.
    /// </para>
    ///
    /// <para>
    /// At least one vertex required. Same vertex sequence on the same
    /// composition ⇒ same row by content-addressed dedup. Per AP-19, the
    /// pipeline's diff against existing physicality rows suppresses
    /// duplicates before COPY.
    /// </para>
    /// </summary>
    void AddIngestionTrajectory(
        EntityHandle parent,
        ReadOnlySpan<TrajectoryVertex> vertices)
        => throw new NotSupportedException(
            "AddIngestionTrajectory is wired by the pipeline's IIngestionBatch implementation in S3. Test doubles can override.");

    /// <summary>
    /// Emit the composition's recorded ingestion trajectory as a
    /// mantissa-packed MULTILINESTRINGZM — one sub-linestring per parallel
    /// / discontinuous / multi-tier segment, vertices within each segment
    /// in trajectory order. Used when a composition has multiple parallel
    /// sub-sequences (audio + transcript, bilingual interlinear), multi-tier
    /// fingerprint views (word tier + grapheme tier of the same sentence in
    /// one row), or discontinuous content (footnote main + footnote body
    /// interleaved).
    ///
    /// <para>
    /// Emits a row with <c>physicality_type = ingestion_trajectory</c> and
    /// <c>geom = MULTILINESTRINGZM</c>. Application reads
    /// <c>GeometryType(geom)</c> and dispatches between LINESTRING vs
    /// MULTILINESTRING.
    /// </para>
    /// </summary>
    void AddIngestionMultiTrajectory(
        EntityHandle parent,
        IReadOnlyList<ReadOnlyMemory<TrajectoryVertex>> subTrajectories)
        => throw new NotSupportedException(
            "AddIngestionMultiTrajectory is wired by the pipeline's IIngestionBatch implementation in S3. Test doubles can override.");

    /// <summary>
    /// Emit one per-token firefly POINTZM — a single ingested model's
    /// projection of <paramref name="parent"/> (typically a <c>word_form</c>
    /// token entity) into the substrate's shared 4D frame. Replaces the
    /// firefly emission embedded in <c>EmbeddingLookupTuplePass</c>.
    ///
    /// <para>
    /// Emits a row with <c>physicality_type = firefly</c>, <c>geom = POINTZM</c>,
    /// and <c>content_hash</c> derived from <paramref name="modelSourceId"/>
    /// so multiple models' fireflies for the same token coexist on the same
    /// entity (one row per model). Post-Procrustes alignment per AP-35.
    /// </para>
    /// </summary>
    void AddFireflyPoint(
        EntityHandle parent,
        long modelSourceId,
        Point4D projection)
        => throw new NotSupportedException(
            "AddFireflyPoint is wired by the pipeline's IIngestionBatch implementation in S3. Test doubles can override.");

    /// <summary>
    /// Append an entity-significance prime row in the given arena, stratified
    /// by attestation_type. Default attestation_type
    /// 'provenance_authority_corroboration' matches ingestion-time priming
    /// where the source's authority is the kind of evidence. Edge
    /// significance is primed in bulk by a separate substrate procedure, not
    /// per-batch.
    /// </summary>
    void AddSignificance(
        EntityHandle entity,
        string contextTypeCode,
        double initialMu,
        string attestationTypeCode = "provenance_authority_corroboration");

    /// <summary>
    /// Record that <paramref name="entity"/> was observed in the given
    /// model_source. Same entity hash appearing in N model sources = one
    /// substrate.entity row, N substrate.entity_model_source rows.
    /// </summary>
    void AddEntityModelSource(EntityHandle entity, long modelSourceId);

    int EntityCount { get; }
    int EdgeCount { get; }
}
