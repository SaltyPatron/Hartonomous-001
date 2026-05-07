using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Recomposition;
using Hartonomous.Core.Query;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Query;
using Hartonomous.Engine.Inference;
using Hartonomous.Engine.Monitoring;
using Hartonomous.Engine.Significance;
using Hartonomous.Engine.Traversal;
using Hartonomous.Recomposers;
using Hartonomous.Api.Endpoints;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();

string connString = builder.Configuration["ConnectionStrings:Hartonomous"]
    ?? Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
    ?? "Host=localhost;Port=5433;Database=hartonomous;Username=hartonomous;Password=hartonomous";

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connString));
builder.Services.AddSingleton<IReferenceDataReader>(sp => new NpgsqlReferenceDataReader(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IReferenceDataWriter>(sp => new NpgsqlReferenceDataWriter(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IJunctionWriter>(sp => new NpgsqlJunctionWriter(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ISessionStore>(sp => new NpgsqlSessionStore(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IHealthCheck>(sp => new SqlHealthCheck(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ITraversal>(sp => new NpgsqlTraversal(
    sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ISignificanceUpdater>(sp => new GlickoSignificanceUpdater(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GlickoSignificanceUpdater>>()));
builder.Services.AddSingleton<NpgsqlEntityReader>(sp => new NpgsqlEntityReader(
    sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IEntityReader>(sp => sp.GetRequiredService<NpgsqlEntityReader>());
builder.Services.AddSingleton<ITextRecompositionReader>(sp => sp.GetRequiredService<NpgsqlEntityReader>());
builder.Services.AddSingleton<IInferenceEngine>(sp => new SubstrateInferenceEngine(
    sp.GetRequiredService<NpgsqlDataSource>(),
    sp.GetRequiredService<IIngestionPipeline>(),
    sp.GetRequiredService<IReferenceDataReader>(),
    sp.GetRequiredService<ILogger<SubstrateInferenceEngine>>()));
builder.Services.AddSingleton<IRecomposer<string>>(sp => new TextRecomposer(
    sp.GetRequiredService<IEntityReader>()));
builder.Services.AddSingleton<IPhysicalityReader>(sp => new NpgsqlPhysicalityReader(
    sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<ISubstrateQuery>(sp => new NpgsqlSubstrateQuery(
    sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddSingleton<IRecomposer<SafetensorsFile>>(sp => new SafetensorsRecomposer(
    sp.GetRequiredService<IEntityReader>(),
    sp.GetService<ITextRecompositionReader>(),
    sp.GetService<IPhysicalityReader>(),
    sp.GetService<ISubstrateQuery>()));

WebApplication app = builder.Build();

app.MapGet("/health", async (IHealthCheck healthCheck, CancellationToken ct) =>
{
    SubstrateHealth health = await healthCheck.GetHealthAsync(ct);
    return Results.Ok(health);
});

app.MapGet("/health/ingestion", async (IHealthCheck healthCheck, CancellationToken ct) =>
{
    var statuses = await healthCheck.GetIngestionStatusAsync(ct);
    return Results.Ok(statuses);
});

app.MapGet("/status", async (ISessionStore sessionStore, CancellationToken ct) =>
{
    var phases = await sessionStore.GetPhaseStatusOverviewAsync(ct);
    var totals = await sessionStore.GetSubstrateTotalsAsync(ct);
    var activeSessions = await sessionStore.GetActiveSessionsAsync(ct);
    return Results.Ok(new { phases, totals, activeSessions });
});

// Spec-defined /api/ endpoint groups.
app.MapEntityEndpoints();
app.MapEdgeEndpoints();
app.MapTraversalEndpoints();
app.MapRecompositionEndpoints();
app.MapSignificanceEndpoints();
app.MapMonitorEndpoints();

app.Run();

