using Hartonomous.Core.Engine;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Inference;
using Hartonomous.Engine.Traversal;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// End-to-end inference: text query → tokenize → resolve seeds against the live
/// substrate via NpgsqlEntityReader → A* traversal via NpgsqlTraversal (which
/// wraps the C-implemented public.traverse_astar) → assemble entity metadata.
/// Exercises the full C# inference engine layer over the real 5M-entity
/// PostgreSQL substrate produced by UCD/UCA + ISO 639 + WordNet + OMW + UD.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001",
    Justification = "Disposable fields are released in IAsyncLifetime.DisposeAsync.")]
public sealed class InferenceEndToEndTests : IAsyncLifetime
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlReferenceDataReader _refReader = null!;
    private NpgsqlEntityReader _entityReader = null!;
    private NpgsqlTraversal _traversal = null!;
    private SubstrateInferenceEngine _engine = null!;

    public Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(ConnectionString());
        _refReader = new NpgsqlReferenceDataReader(_dataSource);
        _entityReader = new NpgsqlEntityReader(_dataSource);
        _traversal = new NpgsqlTraversal(_dataSource, _refReader);
        _engine = new SubstrateInferenceEngine(
            _traversal,
            _entityReader,
            _refReader,
            NullLogger<SubstrateInferenceEngine>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task InferAsync_ResolvesSeeds_AgainstLiveSubstrate()
    {
        // English word from your own regression-case set. The substrate has
        // WordNet lemmas + senses + glosses ingested, so this token should
        // resolve to one or more seed entities.
        InferenceQuery query = new()
        {
            Text = "break",
        };

        InferenceResult result = await _engine.InferAsync(query, CancellationToken.None);

        Assert.NotEmpty(result.SeedEntityIds);
        Console.WriteLine($"[InferAsync] query='break' seeds={result.SeedEntityIds.Count} " +
                          $"paths={result.Paths.Count} nodes_visited={result.NodesVisited} " +
                          $"elapsed={result.Elapsed.TotalMilliseconds:F1}ms");
    }
}
