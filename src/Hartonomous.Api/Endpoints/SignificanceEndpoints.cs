using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Hartonomous.Api.Endpoints;

internal static class SignificanceEndpoints
{
    internal static void MapSignificanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/significance/{entityId}", GetSignificance);
        app.MapGet("/api/significance/{entityId}/neighbors", GetNeighbors);
    }

    private static async Task<IResult> GetSignificance(
        long entityId, string? arena,
        NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT significance_id, arena_code, mu, sigma, volatility, games " +
            "FROM substrate.get_entity_significance($1, $2)", conn);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue((object?)arena ?? DBNull.Value);

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                significanceId = reader.GetInt64(0),
                arena = reader.GetString(1).Trim(),
                mu = reader.GetDouble(2),
                sigma = reader.GetDouble(3),
                volatility = reader.GetDouble(4),
                games = reader.GetInt32(5),
            });
        }

        return Results.Ok(new
        {
            entityId,
            significance = items,
        });
    }

    private static async Task<IResult> GetNeighbors(
        long entityId, string arena, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 20, 1, 200);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT neighbor_entity_id, entity_type_code, mu, sigma " +
            "FROM substrate.get_significant_neighbors($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(arena);
        cmd.Parameters.AddWithValue(pageSize);

        List<object> neighbors = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbors.Add(new
            {
                entityId = reader.GetInt64(0),
                entityTypeName = reader.GetString(1).Trim(),
                mu = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                sigma = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
            });
        }

        return Results.Ok(new
        {
            seedEntityId = entityId,
            arena,
            neighbors,
        });
    }
}
