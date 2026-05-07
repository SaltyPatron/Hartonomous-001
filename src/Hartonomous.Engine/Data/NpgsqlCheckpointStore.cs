using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Persistence for <c>substrate.model_pass_checkpoint</c> via Npgsql.
/// Pure stored-procedure passthrough — no SQL composition.
/// Consolidates the inline SQL from <c>ModelPassCheckpointStore</c>.
/// </summary>
public sealed class NpgsqlCheckpointStore : IModelPassCheckpointStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlCheckpointStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlySet<string>> LoadCompletedPassIdsAsync(long modelSourceId, CancellationToken ct)
    {
        HashSet<string> completed = new(StringComparer.Ordinal);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT pass_id FROM substrate.get_completed_model_passes($1)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, modelSourceId);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            completed.Add(reader.GetString(0));
        }
        return completed;
    }

    public Task MarkCompletedAsync(
        long modelSourceId, string passId, long entityCount, long edgeCount, CancellationToken ct)
        => UpsertAsync(modelSourceId, passId, entityCount, edgeCount, lastError: null, completed: true, ct);

    public Task MarkInFlightAsync(
        long modelSourceId, string passId, long entityCount, long edgeCount, string? lastError, CancellationToken ct)
        => UpsertAsync(modelSourceId, passId, entityCount, edgeCount, lastError, completed: false, ct);

    private async Task UpsertAsync(
        long modelSourceId, string passId, long entityCount, long edgeCount,
        string? lastError, bool completed, CancellationToken ct)
    {
        // SQL signature: substrate.upsert_model_pass_checkpoint(
        //     p_model_source_id INTEGER,
        //     p_pass_name TEXT,
        //     p_status TEXT,           -- 'completed' | 'in_flight' | 'failed'
        //     p_rows_emitted BIGINT,   -- combined entity + edge rows
        //     p_error_message TEXT,
        //     p_extra JSONB)           -- entity_count / edge_count breakdown
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_model_pass_checkpoint($1, $2, $3, $4, $5, $6::jsonb)", conn);
        string status = completed
            ? "completed"
            : (lastError is not null ? "failed" : "in_flight");
        string extraJson = $$"""{"entity_count":{{entityCount}},"edge_count":{{edgeCount}}}""";
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, (int)modelSourceId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, passId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, status);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, entityCount + edgeCount);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, extraJson);
        _ = await cmd.ExecuteScalarAsync(ct);
    }
}
