using Hartonomous.Core.Engine;
using Hartonomous.Core.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hartonomous.Api.Endpoints;

internal static class TraversalEndpoints
{
    internal static void MapTraversalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/traversal", Traverse);
        app.MapPost("/api/infer", Infer);
    }

    private static async Task<IResult> Traverse(
        TraversalQuery query, ITraversal traversal, CancellationToken ct)
    {
        TraversalResult result = await traversal.TraverseAsync(query, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> Infer(
        InferenceQuery query, IInferenceEngine engine, CancellationToken ct)
    {
        InferenceResult result = await engine.InferAsync(query, ct);
        return Results.Ok(result);
    }
}
