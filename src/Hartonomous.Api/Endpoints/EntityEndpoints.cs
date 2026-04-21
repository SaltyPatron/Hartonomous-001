using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Hartonomous.Api.Endpoints;

internal static class EntityEndpoints
{
    internal static void MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/entities/{id}", GetEntityById);
        app.MapGet("/api/entities/by-hash/{hash}", GetEntityByHash);
        app.MapGet("/api/entities", ListEntities);
        app.MapGet("/api/entities/{id}/classifications", GetClassifications);
        app.MapGet("/api/entities/{id}/edges", GetEntityEdges);
    }

    private static async Task<IResult> GetEntityById(
        long id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_id, entity_type_id, entity_type_code, hash " +
            "FROM substrate.get_entity_info($1)", conn);
        cmd.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Entity not found", statusCode: 404, type: "entity-not-found");
        }

        return Results.Ok(new
        {
            entityId = reader.GetInt64(0),
            entityTypeId = reader.GetInt32(1),
            entityTypeName = reader.GetString(2).Trim(),
            hash = Convert.ToHexString((byte[])reader.GetValue(3)).ToLowerInvariant(),
        });
    }

    private static async Task<IResult> GetEntityByHash(
        string hash, NpgsqlDataSource db, CancellationToken ct)
    {
        if (hash.Length != 64)
        {
            return Results.Problem(
                "Hash must be 64 hex characters (32 bytes)", statusCode: 400, type: "invalid-hash");
        }

        byte[] hashBytes;
        try
        {
            hashBytes = Convert.FromHexString(hash);
        }
        catch (FormatException) // BOUNDARY: API input validation — invalid hex from caller.
        {
            return Results.Problem(
                "Invalid hex encoding", statusCode: 400, type: "invalid-hash");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_id, entity_type_id, entity_type_code, hash " +
            "FROM substrate.get_entity_by_hash($1)", conn);
        cmd.Parameters.AddWithValue(hashBytes);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Entity not found", statusCode: 404, type: "entity-not-found");
        }

        return Results.Ok(new
        {
            entityId = reader.GetInt64(0),
            entityTypeId = reader.GetInt32(1),
            entityTypeName = reader.GetString(2).Trim(),
            hash = Convert.ToHexString((byte[])reader.GetValue(3)).ToLowerInvariant(),
        });
    }

    private static async Task<IResult> ListEntities(
        int typeId, long? cursor, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 100, 1, 1000);
        long cursorId = cursor ?? 0;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_id, entity_type_id, entity_type_code, hash " +
            "FROM substrate.list_entities($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(typeId);
        cmd.Parameters.AddWithValue(cursorId);
        cmd.Parameters.AddWithValue(pageSize + 1); // Fetch one extra to detect hasMore.

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                entityId = reader.GetInt64(0),
                entityTypeId = reader.GetInt32(1),
                entityTypeName = reader.GetString(2).Trim(),
                hash = Convert.ToHexString((byte[])reader.GetValue(3)).ToLowerInvariant(),
            });
        }

        bool hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return Results.Ok(new
        {
            items,
            nextCursor = items.Count > 0
                ? ((dynamic)items[^1]).entityId
                : (long?)null,
            hasMore,
        });
    }

    private static async Task<IResult> GetClassifications(
        long id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.get_entity_classifications($1)", conn);
        cmd.Parameters.AddWithValue(id);

        object? result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
        {
            return Results.Ok(new
            {
                entityId = id,
                pos = Array.Empty<object>(),
                languages = Array.Empty<string>(),
                senses = Array.Empty<object>(),
                morphFeatures = Array.Empty<string>(),
            });
        }

        // Server returns JSONB — pass it through directly.
        return Results.Ok(new
        {
            entityId = id,
            classifications = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                (string)result),
        });
    }

    private static async Task<IResult> GetEntityEdges(
        long id, string? direction, int? edgeTypeId, long? cursor, int? limit,
        NpgsqlDataSource db, CancellationToken ct)
    {
        int pageSize = Math.Clamp(limit ?? 100, 1, 1000);
        long cursorId = cursor ?? 0;
        string dir = direction ?? "both";

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_id, edge_type_code " +
            "FROM substrate.get_entity_edge_ids($1, $2, $3, $4, $5)", conn);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(dir);
        cmd.Parameters.AddWithValue((object?)edgeTypeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(cursorId);
        cmd.Parameters.AddWithValue(pageSize + 1);

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                edgeId = reader.GetInt64(0),
                edgeTypeName = reader.GetString(1).Trim(),
            });
        }

        bool hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return Results.Ok(new
        {
            items,
            nextCursor = items.Count > 0
                ? ((dynamic)items[^1]).edgeId
                : (long?)null,
            hasMore,
        });
    }
}
