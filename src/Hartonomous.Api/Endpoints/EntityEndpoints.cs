using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using System.Text.Json;

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
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_hash, classifications FROM substrate.api_entity_by_hash($1)", conn);
        cmd.Parameters.AddWithValue(hashBytes!);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Entity not found", statusCode: 404, type: "entity-not-found");
        }

        return Results.Ok(new
        {
            hash = Convert.ToHexString((byte[])reader.GetValue(3)).ToLowerInvariant(),
            classifications = ReadJson(reader, 1),
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
            if (!TryParseHash(cursor, out cursorHash, out IResult? error))
            {
                return error!;
            }
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_hash, classifications FROM substrate.api_list_entities($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue((object?)typeCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)cursorHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue(pageSize + 1); // Fetch one extra to detect hasMore.

        List<object> items = [];
        List<string> hashes = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string itemHash = Convert.ToHexString((byte[])reader.GetValue(0)).ToLowerInvariant();
            hashes.Add(itemHash);
            items.Add(new
            {
                hash = itemHash,
                classifications = ReadJson(reader, 1),
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
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.api_entity_classifications($1)", conn);
        cmd.Parameters.AddWithValue(hashBytes!);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new
        {
            hash = hash.ToLowerInvariant(),
            classifications = ReadJson(result),
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
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_type_id, edge_type_code, edge_hash, role_code, role_position, provenance_code " +
            "FROM substrate.api_entity_edges($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(hashBytes!);
        cmd.Parameters.AddWithValue(dir);
        cmd.Parameters.AddWithValue((object?)edgeTypeCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue(pageSize);

        List<object> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                edgeTypeId = reader.GetInt32(0),
                edgeTypeCode = reader.GetString(1).Trim(),
                edgeHash = Convert.ToHexString((byte[])reader.GetValue(2)).ToLowerInvariant(),
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

    private static JsonElement ReadJson(NpgsqlDataReader reader, int ordinal) =>
        ReadJson(reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));

    private static JsonElement ReadJson(object? value)
    {
        string json = value is null or DBNull ? "[]" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "[]";
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
