using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// <see cref="IModelPassCheckpointStore"/> backed by the substrate functions in
/// migration 0024. Calls are 1:1 against
/// <c>substrate.upsert_model_pass_checkpoint</c> and
/// <c>substrate.get_completed_model_passes</c>.
/// </summary>
internal sealed class ModelPassCheckpointStore : IModelPassCheckpointStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ModelPassCheckpointStore(NpgsqlDataSource dataSource)
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

    public Task MarkCompletedAsync(long modelSourceId, string passId, long entityCount, long edgeCount, CancellationToken ct)
        => UpsertAsync(modelSourceId, passId, entityCount, edgeCount, lastError: null, completed: true, ct);

    public Task MarkInFlightAsync(long modelSourceId, string passId, long entityCount, long edgeCount, string? lastError, CancellationToken ct)
        => UpsertAsync(modelSourceId, passId, entityCount, edgeCount, lastError, completed: false, ct);

    private async Task UpsertAsync(
        long modelSourceId, string passId, long entityCount, long edgeCount, string? lastError, bool completed, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_model_pass_checkpoint($1, $2, $3, $4, $5, $6)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, modelSourceId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, passId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, entityCount);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, edgeCount);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, completed);
        _ = await cmd.ExecuteScalarAsync(ct);
    }
}
