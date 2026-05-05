using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Monitoring;
using Npgsql;

namespace Hartonomous.Engine.Monitoring;

public sealed class SqlHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    public SqlHealthCheck(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<SubstrateHealth> GetHealthAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new("SELECT substrate.health_summary()", conn);
        object? result = await cmd.ExecuteScalarAsync(ct);

        if (result is not string json)
        {
            return new SubstrateHealth();
        }

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(json);

        Dictionary<string, long> byType = [];
        if (root.TryGetProperty("entitiesByType", out JsonElement ebt))
        {
            foreach (JsonProperty prop in ebt.EnumerateObject())
            {
                byType[prop.Name] = prop.Value.GetInt64();
            }
        }

        Dictionary<string, double> muByArena = [];
        if (root.TryGetProperty("meanMuByArena", out JsonElement mma))
        {
            foreach (JsonProperty prop in mma.EnumerateObject())
            {
                muByArena[prop.Name] = prop.Value.GetDouble();
            }
        }

        return new SubstrateHealth
        {
            TotalEntities = root.TryGetProperty("totalEntities", out JsonElement te) ? te.GetInt64() : 0,
            TotalEdges = root.TryGetProperty("totalEdges", out JsonElement tedge) ? tedge.GetInt64() : 0,
            EntitiesByType = byType,
            MeanMuByArena = muByArena,
            StorageSizeBytes = root.TryGetProperty("storageSizeBytes", out JsonElement ss) ? ss.GetInt64() : 0,
        };
    }

    public async Task<IReadOnlyList<IngestionStatus>> GetIngestionStatusAsync(CancellationToken ct)
    {
        List<IngestionStatus> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT decomposer_code, entities_created, edges_created, " +
            "entities_per_second, is_stuck, last_report " +
            "FROM substrate.ingestion_summary()", conn);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new IngestionStatus
            {
                DecomposerCode = reader.GetString(0),
                EntitiesCreated = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                EdgesCreated = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                EntitiesPerSecond = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                IsStuck = !reader.IsDBNull(4) && reader.GetBoolean(4),
                LastReport = reader.IsDBNull(5) ? DateTimeOffset.MinValue : reader.GetDateTime(5),
            });
        }

        return results;
    }
}
