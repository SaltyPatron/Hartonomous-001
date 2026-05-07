using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using System.Text.Json;

namespace Hartonomous.Api.Endpoints;

internal static class SignificanceEndpoints
{
    internal static void MapSignificanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/significance/{hash}", GetSignificance);
        app.MapGet("/api/significance/{hash}/neighbors", GetNeighbors);
    }

    private static async Task<IResult> GetSignificance(
        string hash, string? arena,
        NpgsqlDataSource db, CancellationToken ct)
    {
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT arena_code, mu, sigma, volatility, games " +
            "FROM substrate.api_entity_significance($1, $2)", conn);
        cmd.Parameters.AddWithValue(hashBytes!);
        cmd.Parameters.AddWithValue((object?)arena ?? DBNull.Value);

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                arena = reader.GetString(0).Trim(),
                mu = reader.GetDouble(1),
                sigma = reader.GetDouble(2),
                volatility = reader.GetDouble(3),
                games = reader.GetInt32(4),
            });
        }

        return Results.Ok(new
        {
            hash = hash.ToLowerInvariant(),
            significance = items,
        });
    }

    private static async Task<IResult> GetNeighbors(
        string hash, string arena, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 20, 1, 200);
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT neighbor_hash, classifications, edge_type_id, edge_type_code, edge_hash, mu " +
            "FROM substrate.api_entity_neighbors($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(hashBytes!);
        cmd.Parameters.AddWithValue(arena);
        cmd.Parameters.AddWithValue(pageSize);

        List<object> neighbors = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbors.Add(new
            {
                hash = Convert.ToHexString((byte[])reader.GetValue(0)).ToLowerInvariant(),
                classifications = ReadJson(reader.GetValue(1)),
                edgeTypeId = reader.GetInt32(2),
                edgeTypeCode = reader.GetString(3).Trim(),
                edgeHash = Convert.ToHexString((byte[])reader.GetValue(4)).ToLowerInvariant(),
                mu = reader.GetDouble(5),
            });
        }

        return Results.Ok(new
        {
            seedHash = hash.ToLowerInvariant(),
            arena,
            neighbors,
        });
    }

    private static bool TryParseHash(string hash, out byte[]? bytes, out IResult? error)
    {
        bytes = null;
        error = null;
        if (hash.Length != 64)
        {
            error = Results.Problem(
                "Hash must be 64 hex characters (32 bytes)", statusCode: 400, type: "invalid-hash");
            return false;
        }
        try
        {
            bytes = Convert.FromHexString(hash);
            return true;
        }
        catch (FormatException)
        {
            error = Results.Problem("Invalid hex encoding", statusCode: 400, type: "invalid-hash");
            return false;
        }
    }

    private static JsonElement ReadJson(object? value)
    {
        string json = value is null or DBNull ? "[]" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "[]";
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
