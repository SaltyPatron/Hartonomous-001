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
        await using NpgsqlCommand cmd = new("SELECT monitor.create_session($1, $2)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, label);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)notes ?? DBNull.Value);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : Guid.Parse(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!);
    }

    public async Task<bool> CloseSessionAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new("SELECT monitor.close_session()", conn);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is bool closed && closed;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct)
    {
        List<SessionSummary> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT session_id, user_label, started_at, ended_at, comparison_count " +
            "FROM monitor.session_summaries ORDER BY started_at DESC", conn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT session_id, user_label, notes, started_at, ended_at, comparison_count " +
            "FROM monitor.session_details WHERE session_id = $1", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, sessionId);
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
        await using NpgsqlCommand cmd = new("CALL monitor.archive_session($1)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Phase status ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, string>> GetPhaseStatusMapAsync(CancellationToken ct)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT phase_code, status FROM monitor.phase_status", conn);
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
        await using NpgsqlCommand cmd = new(
            "CALL monitor.update_phase_status($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, phaseCode);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, status);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)errorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    public async Task ReportProgressAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "CALL monitor.report_progress($1, $2, $3, $4, $5, $6, $7, $8, $9)", conn);

        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, snapshot.DecomposerCode);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, snapshot.CurrentPhase);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, snapshot.CurrentBatch ?? 0);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, snapshot.EntitiesCreated);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, snapshot.EdgesCreated);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)snapshot.CurrentFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Status dashboard ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PhaseStatusRow>> GetPhaseStatusOverviewAsync(CancellationToken ct)
    {
        List<PhaseStatusRow> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT phase_code, status, entity_count, edge_count, duration_seconds " +
            "FROM monitor.phase_status_overview", conn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT total_entities, total_edges, total_physicalities, total_significance_records " +
            "FROM monitor.substrate_dashboard", conn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT session_id, user_label, started_at, comparison_count " +
            "FROM monitor.active_sessions", conn);
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
        await using NpgsqlCommand cmd = new("CALL monitor.snapshot_health()", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── CLI-surface helpers ───────────────────────────────────────────────────

    public async Task<string?> GetHealthSummaryJsonAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new("SELECT substrate.health_summary()", conn);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : result.ToString();
    }

    public async Task ResetPhaseCheckpointAsync(string phaseStr, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using (NpgsqlCommand del = new("DELETE FROM monitor.phase_status WHERE phase_code = $1", conn))
        {
            del.Parameters.AddWithValue(phaseStr);
            await del.ExecuteNonQueryAsync(ct);
        }
        await using NpgsqlCommand trunc = new("TRUNCATE TABLE substrate.model_pass_checkpoint", conn);
        await trunc.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(string EntityType, long EntityCount, long EdgeCount)>> GetSubstrateCountsAsync(CancellationToken ct)
    {
        List<(string, long, long)> rows = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_type, entity_count, edge_count " +
            "FROM monitor.entity_type_counts " +
            "ORDER BY entity_count DESC, entity_type", conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return rows;
    }
}
