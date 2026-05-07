using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Monitoring;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Manages sessions, phase status, and progress reporting via Npgsql.
/// Consolidates the inline SQL from CLI <c>Program.cs</c> (session CRUD),
/// <c>SequentialPhaseRunner</c> (phase status), and
/// <c>DatabaseProgressReporter</c> (progress reporting).
/// </summary>
public sealed class NpgsqlSessionStore : ISessionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlSessionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ── Sessions ─────────────────────────────────────────────────────────────

    public async Task<Guid> CreateSessionAsync(string label, string? notes, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(
            conn,
            MonitorRoutineNames.CreateSession,
            new NpgsqlParameter[]
            {
                CreateParameter(NpgsqlDbType.Text, label),
                CreateParameter(NpgsqlDbType.Text, notes),
            });
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : Guid.Parse(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!);
    }

    public async Task<bool> CloseSessionAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.CloseSession);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is bool closed && closed;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct)
    {
        List<SessionSummary> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.ListSessions);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SessionSummary(
                SessionId: reader.GetGuid(0),
                Label: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                StartedAt: reader.GetDateTime(2),
                EndedAt: reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                ComparisonEventCount: reader.GetInt64(4)));
        }
        return results;
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(
            conn,
            MonitorRoutineNames.SessionDetail,
            new NpgsqlParameter[] { CreateParameter(NpgsqlDbType.Uuid, sessionId) });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SessionDetail(
            SessionId: reader.GetGuid(0),
            Label: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Notes: reader.IsDBNull(2) ? null : reader.GetString(2),
            StartedAt: reader.GetDateTime(3),
            EndedAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            ComparisonEventCount: reader.GetInt64(5));
    }

    public async Task ArchiveSessionAsync(Guid sessionId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateProcedure(
            conn,
            MonitorRoutineNames.ArchiveSession,
            [CreateParameter(NpgsqlDbType.Uuid, sessionId)]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Phase status ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, string>> GetPhaseStatusMapAsync(CancellationToken ct)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.PhaseStatusMap);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }
        return map;
    }

    public async Task UpdatePhaseStatusAsync(
        string phaseCode, string status, string? errorMessage, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateProcedure(
            conn,
            MonitorRoutineNames.UpdatePhaseStatus,
            [
                CreateParameter(NpgsqlDbType.Text, phaseCode),
                CreateParameter(NpgsqlDbType.Text, status),
                CreateParameter(NpgsqlDbType.Text, errorMessage),
            ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    public async Task ReportProgressAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateProcedure(
            conn,
            MonitorRoutineNames.ReportProgress,
            [
                CreateParameter(NpgsqlDbType.Text, snapshot.DecomposerCode),
                CreateParameter(NpgsqlDbType.Text, snapshot.CurrentPhase),
                CreateParameter(NpgsqlDbType.Integer, snapshot.CurrentBatch ?? 0),
                CreateParameter(NpgsqlDbType.Bigint, snapshot.EntitiesCreated),
                CreateParameter(NpgsqlDbType.Bigint, snapshot.EdgesCreated),
                CreateParameter(NpgsqlDbType.Text, snapshot.CurrentFile),
                CreateParameter(NpgsqlDbType.Text, null),
                CreateParameter(NpgsqlDbType.Text, null),
                CreateParameter(NpgsqlDbType.Text, null),
            ]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Status dashboard ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PhaseStatusRow>> GetPhaseStatusOverviewAsync(CancellationToken ct)
    {
        List<PhaseStatusRow> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.PhaseStatusOverviewRows);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PhaseStatusRow(
                PhaseCode: reader.GetString(0),
                Status: reader.GetString(1),
                EntityCount: reader.GetInt64(2),
                EdgeCount: reader.GetInt64(3),
                DurationSeconds: reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }
        return results;
    }

    public async Task<SubstrateTotals?> GetSubstrateTotalsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.SubstrateTotals);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new SubstrateTotals(
            TotalEntities: reader.GetInt64(0),
            TotalEdges: reader.GetInt64(1),
            TotalPhysicalities: reader.GetInt64(2),
            TotalSignificanceRecords: reader.GetInt64(3));
    }

    public async Task<IReadOnlyList<ActiveSessionRow>> GetActiveSessionsAsync(CancellationToken ct)
    {
        List<ActiveSessionRow> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.ActiveSessionRows);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ActiveSessionRow(
                SessionId: reader.GetGuid(0),
                Label: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                StartedAt: reader.GetDateTime(2),
                ComparisonEventCount: reader.GetInt64(3)));
        }
        return results;
    }

    public async Task SnapshotHealthAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateProcedure(conn, MonitorRoutineNames.SnapshotHealth, []);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── CLI-surface helpers ───────────────────────────────────────────────────

    public async Task<string?> GetHealthSummaryJsonAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(conn, SubstrateFunctionNames.HealthSummary);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task ResetPhaseCheckpointAsync(string phaseStr, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateProcedure(
            conn,
            MonitorRoutineNames.ResetPhaseCheckpoint,
            [CreateParameter(NpgsqlDbType.Text, phaseStr)]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(string EntityType, long EntityCount, long EdgeCount)>> GetSubstrateCountsAsync(CancellationToken ct)
    {
        List<(string, long, long)> rows = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlMonitorCommand.CreateFunction(conn, MonitorRoutineNames.EntityTypeCountRows);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return rows;
    }

    private static NpgsqlParameter CreateParameter(NpgsqlDbType type, object? value)
        => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };
}
