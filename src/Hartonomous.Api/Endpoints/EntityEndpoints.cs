using Microsoft.AspNetCore.Builder;
using Hartonomous.Core.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Hartonomous.Api.Endpoints;

internal static class EntityEndpoints
{
    internal static void MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/entities/by-hash/{hash}", GetEntityByHash);
        app.MapGet("/api/entities", ListEntities);
        app.MapGet("/api/entities/{hash}/classifications", GetClassifications);
        app.MapGet("/api/entities/{hash}/edges", GetEntityEdges);
    }

    private static async Task<IResult> GetEntityByHash(
        string hash, NpgsqlDataSource db, CancellationToken ct)
    {
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEntityByHash,
            hashBytes!);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Entity not found", statusCode: 404, type: "entity-not-found");
        }

        return Results.Ok(new
        {
            hash = ApiHash.ToHex((byte[])reader.GetValue(0)),
            classifications = ApiJson.Read(reader, 1),
        });
    }

    private static async Task<IResult> ListEntities(
        string? typeCode, string? cursor, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 100, 1, 1000);
        byte[]? cursorHash = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!ApiHash.TryParse(cursor, out cursorHash, out IResult? error))
            {
                return error!;
            }
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiListEntities,
            typeCode,
            cursorHash,
            pageSize + 1);

        List<object> items = [];
        List<string> hashes = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string itemHash = ApiHash.ToHex((byte[])reader.GetValue(0));
            hashes.Add(itemHash);
            items.Add(new
            {
                hash = itemHash,
                classifications = ApiJson.Read(reader, 1),
            });
        }

        bool hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
            hashes.RemoveAt(hashes.Count - 1);
        }

        return Results.Ok(new
        {
            items,
            nextCursor = hasMore && hashes.Count > 0 ? hashes[^1] : null,
            hasMore,
        });
    }

    private static async Task<IResult> GetClassifications(
        string hash, NpgsqlDataSource db, CancellationToken ct)
    {
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEntityClassifications,
            hashBytes!);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new
        {
            hash = hash.ToLowerInvariant(),
            classifications = ApiJson.Read(result),
        });
    }

    private static async Task<IResult> GetEntityEdges(
        string hash, string? direction, string? edgeTypeCode, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 100, 1, 1000);
        string dir = direction ?? "both";
        if (dir is not ("both" or "in" or "out"))
        {
            return Results.Problem("direction must be one of: both, in, out", statusCode: 400, type: "invalid-direction");
        }
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEntityEdges,
            hashBytes!,
            dir,
            edgeTypeCode,
            pageSize);

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                edgeTypeId = reader.GetInt32(0),
                edgeTypeCode = reader.GetString(1).Trim(),
                edgeHash = ApiHash.ToHex((byte[])reader.GetValue(2)),
                roleCode = reader.GetString(3).Trim(),
                rolePosition = reader.GetInt32(4),
                provenanceCode = reader.GetString(5).Trim(),
            });
        }

        return Results.Ok(new
        {
            hash = hash.ToLowerInvariant(),
            items,
        });
    }
}
