using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Bulk substrate-existence probes used at the producer-to-pipeline funnel
/// boundary. Decomposers MUST call these once per kind per chunk and emit
/// only the diff <c>candidates ∖ existing</c> — blind emission relying on
/// <c>ON CONFLICT DO NOTHING</c> to clean up duplicates causes 30:1+
/// amplification under heavy seed loads (AP-19).
///
/// <para>
/// Implementations open their own short-lived <see cref="Npgsql.NpgsqlConnection"/>
/// per call so concurrent callers across N producer worker tasks (AP-24) do
/// not serialize on a single shared connection. Each method is a single
/// round trip — composite-PK probes use <c>WHERE (pk_cols) = ANY(unnest(...))</c>
/// at the SQL boundary.
/// </para>
///
/// <para>
/// Extracted from <c>IIngestionPipeline</c> per the S3.B pipeline split so
/// the dedup contract is wired through DI rather than tangled in the pipeline
/// surface. <c>StreamingIngestionPipeline</c> consumes one of these as a
/// constructor dependency and delegates its <c>GetExisting*Async</c> methods
/// to it.
/// </para>
/// </summary>
public interface ISubstrateExistenceChecker
{
    /// <summary>
    /// Of the supplied entity hashes, return the subset that already exist
    /// in <c>substrate.entity</c>. Missing set = input ∖ result.
    /// </summary>
    Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<Hash32> hashes, CancellationToken ct);

    /// <summary>
    /// Of the supplied <c>(entity_hash, entity_type_code, provenance_code)</c>
    /// tuples, return the subset that already exist in
    /// <c>substrate.entity_classification</c>.
    /// </summary>
    Task<HashSet<EntityClassificationKey>> GetExistingEntityClassificationsAsync(
        IReadOnlyCollection<EntityClassificationKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied <c>(edge_type_code, edge_hash)</c> tuples, return the
    /// subset that already exist in <c>substrate.edge</c>.
    /// </summary>
    Task<HashSet<EdgeKey>> GetExistingEdgesAsync(
        IReadOnlyCollection<EdgeKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied edge-member PK tuples, return the subset that already
    /// exists in <c>substrate.edge_member</c>.
    /// </summary>
    Task<HashSet<EdgeMemberKey>> GetExistingEdgeMembersAsync(
        IReadOnlyCollection<EdgeMemberKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied <c>(physicality_type_code, entity_hash, content_hash)</c>
    /// tuples, return the subset that already exist in
    /// <c>substrate.physicality</c>.
    /// </summary>
    Task<HashSet<PhysicalityKey>> GetExistingPhysicalitiesAsync(
        IReadOnlyCollection<PhysicalityKey> tuples, CancellationToken ct);
}
