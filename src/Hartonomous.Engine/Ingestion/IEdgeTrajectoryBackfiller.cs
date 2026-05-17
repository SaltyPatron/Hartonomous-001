using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Backfills <c>substrate.edge.geom</c> as a LINESTRING4D through participants'
/// real centroids in role order, for any edge whose trajectory geometry was
/// not built inline at insert time.
///
/// <para>
/// Edges whose participants' POINT4D centroids are present in the producer's
/// in-batch centroid map get their <c>geom</c> built inline by
/// <c>StreamingIngestionPipeline</c>. Edges whose participants span batches
/// (or arrive before their participants' physicality rows are persisted) get
/// NULL <c>geom</c>; this backfiller fills them at end of phase by calling
/// <c>substrate.populate_edge_trajectories</c>.
/// </para>
///
/// <para>
/// Edge trajectories use real metric coordinates (canonical shape for
/// <c>frechet_4d_geom</c> over relation fingerprints — e.g. analogy completion
/// <c>gender_correspondence(king, queen) ≈ gender_correspondence(man, woman)</c>),
/// not mantissa-packed identity bits. Compare to <c>content</c>
/// which uses bit-banged vertices for child identity recovery.
/// </para>
/// </summary>
public interface IEdgeTrajectoryBackfiller
{
    /// <summary>
    /// Populate <c>substrate.edge.geom</c> for every edge whose trajectory is
    /// not yet set. Idempotent; safe to call repeatedly. Called once at the
    /// end of a decomposition phase before
    /// <c>ISignificancePrimer.PrimeAllSignificanceAsync</c>.
    /// </summary>
    Task PopulateEdgeTrajectoriesAsync(CancellationToken ct);
}
