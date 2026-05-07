using Microsoft.AspNetCore.Builder;
using Hartonomous.Core.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

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
        if (!ApiHash.TryParse(hash, out byte[]? hashBytes, out IResult? error))
        {
            return error!;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApiEdgeByHash,
            edgeTypeCode,
            hashBytes!);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.Problem("Edge not found", statusCode: 404, type: "edge-not-found");
        }

        return Results.Ok(new
        {
            edgeTypeId = reader.GetInt32(0),
            edgeTypeCode = reader.GetString(1).Trim(),
            hash = ApiHash.ToHex((byte[])reader.GetValue(2)),
            provenance = reader.GetString(3).Trim(),
            members = ApiJson.Read(reader.GetValue(4)),
        });
    }
}
