using System;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hartonomous.Api.Endpoints;

internal static class RecompositionEndpoints
{
    internal static void MapRecompositionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/recompose", RecomposeText);
    }

    private static async Task<IResult> RecomposeText(
        RecomposeRequest request,
        IRecomposer<string> recomposer,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.EntityTypeCode) || string.IsNullOrEmpty(request.EntityHashHex))
        {
            return Results.Problem(
                "entityTypeCode and entityHashHex are required",
                statusCode: 400,
                type: "invalid-handle");
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString(request.EntityHashHex);
        }
        catch (FormatException) // BOUNDARY: HTTP request validation surface — invalid hex from the client must surface as 400, not 500.
        {
            return Results.Problem(
                "entityHashHex must be 64 hex chars",
                statusCode: 400,
                type: "invalid-hash");
        }

        EntityHandle handle = new(hash, request.EntityTypeCode);

        RecompositionOptions options = new()
        {
            MaxDepth = request.MaxDepth ?? 50,
        };

        string text = await recomposer.RecomposeAsync(handle, options, ct);

        return Results.Ok(new
        {
            entity = handle.ToString(),
            text,
            maxDepth = options.MaxDepth,
        });
    }

    internal sealed record RecomposeRequest(string EntityTypeCode, string EntityHashHex, int? MaxDepth);
}
