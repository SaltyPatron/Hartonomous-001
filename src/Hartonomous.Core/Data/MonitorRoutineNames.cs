using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Data;

public static class MonitorRoutineNames
{
    public const string ActiveSessionRows = "monitor.active_session_rows";
    public const string ArchiveSession = "monitor.archive_session";
    public const string CloseSession = "monitor.close_session";
    public const string CreateSession = "monitor.create_session";
    public const string EntityTypeCountRows = "monitor.entity_type_count_rows";
    public const string IngestionStatusRows = "monitor.ingestion_status_rows";
    public const string ListSessions = "monitor.list_sessions";
    public const string PhaseStatusMap = "monitor.phase_status_map";
    public const string PhaseStatusOverviewRows = "monitor.phase_status_overview_rows";
    public const string ReportProgress = "monitor.report_progress";
    public const string ResetPhaseCheckpoint = "monitor.reset_phase_checkpoint";
    public const string SessionDetail = "monitor.session_detail";
    public const string SnapshotHealth = "monitor.snapshot_health";
    public const string SubstrateTotals = "monitor.substrate_totals";
    public const string UpdatePhaseStatus = "monitor.update_phase_status";

    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        ActiveSessionRows,
        ArchiveSession,
        CloseSession,
        CreateSession,
        EntityTypeCountRows,
        IngestionStatusRows,
        ListSessions,
        PhaseStatusMap,
        PhaseStatusOverviewRows,
        ReportProgress,
        ResetPhaseCheckpoint,
        SessionDetail,
        SnapshotHealth,
        SubstrateTotals,
        UpdatePhaseStatus,
    };

    public static void AssertAllowlisted(string routineName)
    {
        if (!Allowlist.Contains(routineName))
        {
            throw new InvalidOperationException(
                $"Monitor routine name '{routineName}' is not in the allowlist. " +
                "Add it to MonitorRoutineNames.Allowlist before calling.");
        }
    }
}
