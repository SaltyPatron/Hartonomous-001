using Hartonomous.Core.Data;
using Hartonomous.Core.Monitoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hartonomous.Api.Endpoints;

internal static class MonitorEndpoints
{
    internal static void MapMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/monitor/health", GetHealth);
        app.MapGet("/api/monitor/ingestion", GetIngestionStatus);
        app.MapGet("/api/monitor/progress/{phaseCode}", GetProgress);
    }

    private static async Task<IResult> GetHealth(
        IHealthCheck healthCheck, CancellationToken ct)
    {
        SubstrateHealth health = await healthCheck.GetHealthAsync(ct);
        return Results.Ok(health);
    }

    private static async Task<IResult> GetIngestionStatus(
        IHealthCheck healthCheck, CancellationToken ct)
    {
        var statuses = await healthCheck.GetIngestionStatusAsync(ct);
        return Results.Ok(statuses);
    }

    private static async Task<IResult> GetProgress(
        string phaseCode, ISessionStore sessionStore, CancellationToken ct)
    {
        var phases = await sessionStore.GetPhaseStatusOverviewAsync(ct);
        var match = phases.FirstOrDefault(p =>
            string.Equals(p.PhaseCode, phaseCode, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return Results.Problem(
                $"Phase '{phaseCode}' not found",
                statusCode: 404,
                type: "phase-not-found");
        }

        return Results.Ok(match);
    }
}
