using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Monitoring;

namespace Hartonomous.Core.Data;

/// <summary>
/// Manages ingestion sessions, phase status persistence, and progress reporting
/// in the <c>monitor</c> schema. Consolidates the inline SQL currently spread
/// across CLI <c>Program.cs</c>, <c>SequentialPhaseRunner</c>, and
/// <c>DatabaseProgressReporter</c>.
/// </summary>
public interface ISessionStore
{
    // ── Sessions ─────────────────────────────────────────────────────────────

    Task<Guid> CreateSessionAsync(string label, string? notes, CancellationToken ct);

    Task<bool> CloseSessionAsync(CancellationToken ct);

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct);

    Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId, CancellationToken ct);

    Task ArchiveSessionAsync(Guid sessionId, CancellationToken ct);

    // ── Phase status ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read all rows from <c>monitor.phase_status</c> as <c>(phase_code → status)</c>.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetPhaseStatusMapAsync(CancellationToken ct);

    /// <summary>
    /// Persist a phase status transition via <c>monitor.update_phase_status</c>.
    /// </summary>
    Task UpdatePhaseStatusAsync(string phaseCode, string status, string? errorMessage, CancellationToken ct);

    // ── Progress ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Report a decomposer progress snapshot via <c>monitor.report_progress</c>.
    /// </summary>
    Task ReportProgressAsync(ProgressSnapshot snapshot, CancellationToken ct);

    // ── Status dashboard ─────────────────────────────────────────────────────

    /// <summary>
    /// Read phase overview rows from <c>monitor.phase_status</c> with entity/edge counts.
    /// </summary>
    Task<IReadOnlyList<PhaseStatusRow>> GetPhaseStatusOverviewAsync(CancellationToken ct);

    /// <summary>
    /// Read aggregate totals from <c>monitor.substrate_dashboard</c>.
    /// </summary>
    Task<SubstrateTotals?> GetSubstrateTotalsAsync(CancellationToken ct);

    /// <summary>
    /// Read active sessions from <c>monitor.active_sessions</c>.
    /// </summary>
    Task<IReadOnlyList<ActiveSessionRow>> GetActiveSessionsAsync(CancellationToken ct);

    /// <summary>
    /// Capture a health snapshot via <c>monitor.snapshot_health()</c>.
    /// </summary>
    Task SnapshotHealthAsync(CancellationToken ct);
}
