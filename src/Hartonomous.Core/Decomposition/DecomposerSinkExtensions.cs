using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Streaming-sink emission helpers, exposed as extension methods on
/// <see cref="IRecordSink"/>. The substrate's single ingestion funnel:
/// decomposers compute deterministic facts and emit them here; the pipeline
/// owns batching, COPY drains, backpressure, edge identity calculation
/// (via <see cref="Blake3.ComputeEdgeHash"/>), and significance event
/// persistence.
///
/// <para>
/// Replaces the protected <c>Emit*Async</c> helpers on
/// <see cref="BaseDecomposer"/>. Existing decomposers continue to use the
/// base-class versions during S2/S3; new code uses these extensions, and S5
/// deletes the base-class duplicates. Both encodings are deliberately
/// byte-for-byte equivalent so the migration is purely mechanical.
/// </para>
/// </summary>
public static class DecomposerSinkExtensions
{
    /// <summary>
    /// The <c>significance_context</c> arena Glicko-2 events fire under when
    /// a decomposer emits an edge with the default provenance-authority
    /// attestation. Matches the constant in
    /// <see cref="BaseDecomposer"/>; both must agree until the BaseDecomposer
    /// helper is deleted in S5.
    /// </summary>
    private const string SourceAuthorityContext = "source_authority";

    /// <summary>
    /// The <c>attestation_type</c> code default-rated edges fire Glicko
    /// events under. Same as <see cref="BaseDecomposer"/>.
    /// </summary>
    private const string ProvenanceAuthorityAttestation = "provenance_authority_corroboration";

    /// <summary>
    /// Per-event Glicko weight applied to the bundled
    /// <c>provenance_authority_corroboration</c> rating event that fires
    /// alongside every default edge emission.
    /// </summary>
    private const double ProvenanceAuthorityEventWeight = 0.8;

    /// <summary>
    /// Emit one entity into the streaming sink. Returns an
    /// <see cref="EntityHandle"/> carrying the (type, hash) FK so downstream
    /// emissions referencing this entity can flow without rehashing.
    /// </summary>
    public static async ValueTask<EntityHandle> EmitEntityAsync(
        this IRecordSink sink,
        Hash32 hash,
        string entityTypeCode,
        string provenanceCode,
        CancellationToken ct)
    {
        await sink.EmitAsync(new EntityRecord(entityTypeCode, hash, provenanceCode), ct);
        return new EntityHandle(hash, entityTypeCode);
    }

    /// <summary>
    /// Emit one edge plus its members. Computes the edge identity via
    /// <see cref="Blake3.ComputeEdgeHash"/> against the role-ordered
    /// participant hashes (matching the substrate-side edge PK
    /// computation in <c>substrate.edge</c>). Members are emitted as
    /// separate <see cref="EdgeMemberRecord"/>s; a bundled
    /// <see cref="EdgeRatingEventRecord"/> fires the
    /// <c>provenance_authority_corroboration</c> Glicko event on the
    /// <c>source_authority</c> arena so the edge's initial mu reflects the
    /// provenance prior.
    /// </summary>
    public static async ValueTask EmitEdgeAsync(
        this IRecordSink sink,
        string edgeTypeCode,
        string provenanceCode,
        int edgeTypeId,
        IReadOnlyList<EdgeMemberSpec> members,
        CancellationToken ct)
    {
        // Sort by Position so EdgeHash is deterministic regardless of
        // caller's emission order (matches IngestionBatch.AddEdge).
        EdgeMemberSpec[] sorted = new EdgeMemberSpec[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            sorted[i] = members[i];
        }
        Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));

        Hash32[] orderedHashes = new Hash32[sorted.Length];
        for (int j = 0; j < sorted.Length; j++)
        {
            orderedHashes[j] = sorted[j].Entity.Hash;
        }
        Hash32 edgeHash = Blake3.ComputeEdgeHash(edgeTypeId, orderedHashes);

        await sink.EmitAsync(new EdgeRecord(edgeTypeCode, edgeHash, provenanceCode), ct);
        for (int j = 0; j < sorted.Length; j++)
        {
            await sink.EmitAsync(new EdgeMemberRecord(
                edgeTypeCode,
                edgeHash,
                sorted[j].Entity.Hash,
                sorted[j].RoleCode,
                sorted[j].Position), ct);
        }
        await sink.EmitAsync(new EdgeRatingEventRecord(
            SourceAuthorityContext,
            ProvenanceAuthorityAttestation,
            edgeTypeCode,
            edgeHash,
            Score: 1.0,
            Weight: ProvenanceAuthorityEventWeight), ct);
    }

    /// <summary>
    /// Resolve <paramref name="edgeTypeCode"/> to its substrate id through
    /// the caller's local code → id map (the
    /// <c>reference_data_reader</c> cache the decomposer warmed up at
    /// phase boundary), then dispatch to the strongly-typed
    /// <see cref="EmitEdgeAsync(IRecordSink, string, string, int, IReadOnlyList{EdgeMemberSpec}, CancellationToken)"/>
    /// overload.
    /// </summary>
    public static ValueTask EmitEdgeAsync(
        this IRecordSink sink,
        IReadOnlyDictionary<string, int> edgeTypeIdMap,
        string edgeTypeCode,
        string provenanceCode,
        IReadOnlyList<EdgeMemberSpec> members,
        CancellationToken ct)
    {
        if (!edgeTypeIdMap.TryGetValue(edgeTypeCode, out int edgeTypeId))
        {
            throw new InvalidOperationException($"Unknown edge_type code '{edgeTypeCode}'.");
        }
        return sink.EmitEdgeAsync(edgeTypeCode, provenanceCode, edgeTypeId, members, ct);
    }

    /// <summary>
    /// Emit one junction row (entity_pos, entity_language,
    /// entity_morph_feature, codepoint_property, model_architecture_class,
    /// entity_lexname, tensor_tensor_role, pattern_deprel). Mu is non-null
    /// only for Glicko-bearing junctions (entity_pos, pattern_deprel);
    /// non-Glicko junctions ignore the value at the drain boundary.
    /// </summary>
    public static ValueTask EmitJunctionAsync(
        this IRecordSink sink,
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu,
        CancellationToken ct,
        string attestationTypeCode = "lexical_curated_relation")
        => sink.EmitAsync(new JunctionRecord(
            junctionTable, entity.Hash, referenceId, attestationTypeCode, mu), ct);

    /// <summary>
    /// Emit one physicality row with a native PostGIS geometry payload.
    /// <paramref name="centroid"/> is the entity's representative POINTZM
    /// — equal to the point itself for POINTZM physicalities, equal to the
    /// unweighted 4D mean of vertices for LINESTRINGZM /
    /// MULTILINESTRINGZM. The pipeline uses this for inline edge-trajectory
    /// construction.
    /// </summary>
    public static ValueTask EmitPhysicalityAsync(
        this IRecordSink sink,
        string physicalityTypeCode,
        EntityHandle entity,
        byte[] geometry,
        Point4D centroid,
        CancellationToken ct)
    {
        Hash32 contentHash = Blake3.Hash32(geometry.AsSpan());
        return sink.EmitAsync(new PhysicalityRecord(
            physicalityTypeCode,
            entity.Hash,
            contentHash,
            geometry,
            centroid), ct);
    }

    /// <summary>
    /// Emit one entity_significance row with an initial Mu. Stratified by
    /// attestation_type so corpus / model / lexicon / outcome evidence
    /// accumulates separately on the same entity per AP-22.
    /// </summary>
    public static ValueTask EmitEntitySignificanceAsync(
        this IRecordSink sink,
        EntityHandle entity,
        string contextTypeCode,
        double initialMu,
        CancellationToken ct,
        string attestationTypeCode = "provenance_authority_corroboration")
        => sink.EmitAsync(new EntitySignificanceRecord(
            contextTypeCode, attestationTypeCode, entity.Hash, initialMu), ct);

    /// <summary>
    /// Emit one <c>entity_model_source</c> lineage row recording that
    /// <paramref name="entity"/> was observed in the given model_source.
    /// </summary>
    public static ValueTask EmitEntityModelSourceAsync(
        this IRecordSink sink,
        EntityHandle entity,
        long modelSourceId,
        CancellationToken ct)
        => sink.EmitAsync(new EntityModelSourceRecord(entity.Hash, modelSourceId), ct);
}
