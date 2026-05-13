using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// End-of-phase priming of <c>substrate.edge_significance</c> against every
/// arena currently present in <c>substrate.significance_context</c>. AP-1
/// compliant by construction: the cross-product runs against ALL arenas at
/// call time — no <c>WHERE context_type_id IN (...)</c> filter, no hardcoded
/// arena subset, no cherry-picking. New arenas added after a phase has run
/// auto-backfill into existing edges on the next prime sweep.
///
/// <para>
/// One default-mu row per (arena, edge_type, edge_hash) triplet that lacks a
/// row. Glicko events fired during ingestion accumulate on top of these
/// rows; arenas added later start from default-mu / max-sigma and tighten
/// as cross-source corroboration arrives.
/// </para>
/// </summary>
public interface ISignificancePrimer
{
    /// <summary>
    /// Prime missing per-arena <c>edge_significance</c> rows for every edge
    /// the substrate has stored. Called once at the end of a decomposition
    /// phase after <c>IEdgeTrajectoryBackfiller.PopulateEdgeTrajectoriesAsync</c>.
    /// </summary>
    Task PrimeAllSignificanceAsync(CancellationToken ct);
}
