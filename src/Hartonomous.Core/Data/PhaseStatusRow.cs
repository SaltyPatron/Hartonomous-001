namespace Hartonomous.Core.Data;

/// <summary>
/// Projection of a <c>monitor.phase_status</c> row including entity/edge counts and duration.
/// </summary>
public sealed record PhaseStatusRow(
    string PhaseCode,
    string Status,
    long EntityCount,
    long EdgeCount,
    int? DurationSeconds);
