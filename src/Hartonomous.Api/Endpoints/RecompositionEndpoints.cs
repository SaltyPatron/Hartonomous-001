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
        if (request.EntityId <= 0)
        {
            return Results.Problem(
                "entityId must be a positive integer",
                statusCode: 400,
                type: "invalid-entity-id");
        }

        RecompositionOptions options = new()
        {
            MaxDepth = request.MaxDepth ?? 50,
        };

        string text = await recomposer.RecomposeAsync(request.EntityId, options, ct);

        return Results.Ok(new
        {
            entityId = request.EntityId,
            text,
            maxDepth = options.MaxDepth,
        });
    }

    internal sealed record RecomposeRequest(long EntityId, int? MaxDepth);
}
