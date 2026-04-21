using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Hartonomous.Api.Endpoints;

internal static class EdgeEndpoints
{
    internal static void MapEdgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/edges/{id}", GetEdgeById);
    }

    private static async Task<IResult> GetEdgeById(
        long id, NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_id, edge_type_id, edge_type_code, hash, provenance_code, members " +
            "FROM substrate.get_edge_info($1)", conn);
        cmd.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Edge not found", statusCode: 404, type: "edge-not-found");
        }

        return Results.Ok(new
        {
            edgeId = reader.GetInt64(0),
            edgeTypeId = reader.GetInt32(1),
            edgeTypeName = reader.GetString(2).Trim(),
            hash = Convert.ToHexString((byte[])reader.GetValue(3)).ToLowerInvariant(),
            provenance = reader.GetString(4).Trim(),
            members = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                reader.GetString(5)),
        });
    }
}
