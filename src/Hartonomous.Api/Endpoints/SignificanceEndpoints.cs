using Microsoft.AspNetCore.Builder;
using Hartonomous.Core.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

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
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEntitySignificance,
            new object?[] { hashBytes!, arena });

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
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEntityNeighbors,
            new object?[] { hashBytes!, arena, pageSize });

        List<object> neighbors = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbors.Add(new
            {
                hash = ApiHash.ToHex((byte[])reader.GetValue(0)),
                classifications = ApiJson.Read(reader.GetValue(1)),
                edgeTypeId = reader.GetInt32(2),
                edgeTypeCode = reader.GetString(3).Trim(),
                edgeHash = ApiHash.ToHex((byte[])reader.GetValue(4)),
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
}
