using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Thin wrapper around substrate.upsert_* / link_entity_model_sources functions.
/// All DDL-level logic (ON CONFLICT, length checks, bulk link semantics) lives in
/// SQL migration 0021 — this file only marshals parameters.
/// </summary>
internal sealed class SafetensorsReferenceTableWriter : BaseReferenceTableWriter
{
    public SafetensorsReferenceTableWriter(string connectionString) : base(connectionString)
    {
    }

    public async Task<Dictionary<string, int>> LoadTensorRoleMapAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> map = new(64, StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new(
            "SELECT id, code FROM substrate.tensor_role", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task<int> EnsureArchitectureClassAsync(string code, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_architecture_class($1)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, code);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<int> EnsureModelRegistryAsync(string code, string displayName, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_model_registry($1, $2)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, code);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, displayName);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<int> EnsureModelPublisherAsync(
        int registryId, string slug, string? displayName, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_model_publisher($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, registryId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, slug);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Varchar, (object?)displayName ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> EnsureModelSourceAsync(
        int registryId, int publisherId, string modelSlug, byte[] revision, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.upsert_model_source($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, registryId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, publisherId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, modelSlug);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, revision);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
