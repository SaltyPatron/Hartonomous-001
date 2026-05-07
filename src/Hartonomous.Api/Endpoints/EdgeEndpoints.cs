using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using System.Text.Json;

namespace Hartonomous.Api.Endpoints;

internal static class EdgeEndpoints
{
    internal static void MapEdgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/edges/{edgeTypeCode}/{hash}", GetEdgeByHash);
    }

    private static async Task<IResult> GetEdgeByHash(
        string edgeTypeCode, string hash, NpgsqlDataSource db, CancellationToken ct)
    {
        if (!TryParseHash(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_type_id, edge_type_code, edge_hash, provenance_code, members " +
            "FROM substrate.api_edge_by_hash($1, $2)", conn);
        cmd.Parameters.AddWithValue(edgeTypeCode);
        cmd.Parameters.AddWithValue(hashBytes!);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Edge not found", statusCode: 404, type: "edge-not-found");
        }

        return Results.Ok(new
        {
            edgeTypeId = reader.GetInt32(0),
            edgeTypeCode = reader.GetString(1).Trim(),
            hash = Convert.ToHexString((byte[])reader.GetValue(2)).ToLowerInvariant(),
            provenance = reader.GetString(3).Trim(),
            members = ReadJson(reader.GetValue(4)),
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
