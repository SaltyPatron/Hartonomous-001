namespace Hartonomous.Core.Data;

/// <summary>
/// Aggregate counts from <c>monitor.substrate_dashboard</c>.
/// </summary>
public sealed record SubstrateTotals(
    long TotalEntities,
    long TotalEdges,
    long TotalPhysicalities,
    long TotalSignificanceRecords);
