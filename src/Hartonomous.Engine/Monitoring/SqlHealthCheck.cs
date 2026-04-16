using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Hartonomous.Core.Monitoring;

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

        long totalEntities = await ScalarLongAsync(conn, "SELECT COUNT(*) FROM substrate.entity", ct);
        long totalEdges = await ScalarLongAsync(conn, "SELECT COUNT(*) FROM substrate.edge", ct);

        Dictionary<string, long> byType = [];
        await using (NpgsqlCommand cmd = new(
            "SELECT et.code, COUNT(*) FROM substrate.entity e " +
            "JOIN substrate.entity_type et ON et.id = e.entity_type_id " +
            "GROUP BY et.code ORDER BY COUNT(*) DESC LIMIT 20", conn))
        {
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                byType[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        Dictionary<string, double> muByArena = [];
        await using (NpgsqlCommand cmd = new(
            "SELECT sc.code, AVG(s.mu) FROM substrate.significance s " +
            "JOIN substrate.significance_context sc ON sc.id = s.context_type_id " +
            "GROUP BY sc.code", conn))
        {
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                muByArena[reader.GetString(0)] = reader.GetDouble(1);
            }
        }

        long storageBytes = await ScalarLongAsync(conn,
            "SELECT pg_database_size(current_database())", ct);

        return new SubstrateHealth
        {
            TotalEntities = totalEntities,
            TotalEdges = totalEdges,
            EntitiesByType = byType,
            MeanMuByArena = muByArena,
            StorageSizeBytes = storageBytes,
        };
    }

    public async Task<IReadOnlyList<IngestionStatus>> GetIngestionStatusAsync(CancellationToken ct)
    {
        List<IngestionStatus> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT decomposer_code, " +
            "SUM(entities_ingested), SUM(edges_created), " +
            "SUM(entities_ingested) / GREATEST(EXTRACT(EPOCH FROM (MAX(completed_at) - MIN(started_at))), 1), " +
            "bool_or(status = 'running' AND started_at < now() - interval '5 minutes'), " +
            "MAX(completed_at) " +
            "FROM monitor.ingestion_progress GROUP BY decomposer_code", conn);

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

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }
}
