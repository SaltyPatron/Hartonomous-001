using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Recomposition;
using Hartonomous.Decomposers.Text;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Text;
using Hartonomous.Recomposers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// End-to-end vertical slice: write a text file, run TextDecomposer through the
/// Npgsql ingestion pipeline, recompose the document via TextRecomposer, assert
/// the recomposed bytes equal the input, then re-run and assert no new entities
/// are created (Law #6 idempotency).
///
/// Requires a running PostgreSQL with migrations applied and at minimum the UCD
/// codepoint property seed loaded.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001",
    Justification = "Disposable fields are released in IAsyncLifetime.DisposeAsync.")]
public sealed class TextRoundTripTests : IAsyncLifetime
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlReferenceDataReader _refReader = null!;
    private NpgsqlEntityReader _entityReader = null!;
    private NpgsqlIngestionPipeline _pipeline = null!;
    private NpgsqlCodepointPropertiesCache _cpProps = null!;
    private string _tempFile = null!;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(ConnectionString());
        _refReader = new NpgsqlReferenceDataReader(_dataSource);
        _entityReader = new NpgsqlEntityReader(_dataSource);
        _pipeline = new NpgsqlIngestionPipeline(
            ConnectionString(),
            _refReader,
            NullLogger<NpgsqlIngestionPipeline>.Instance);
        _cpProps = await NpgsqlCodepointPropertiesCache.LoadAsync(
            ConnectionString(),
            NullLogger<NpgsqlCodepointPropertiesCache>.Instance,
            CancellationToken.None);

        _tempFile = Path.Combine(
            Path.GetTempPath(),
            $"hartonomous_vs_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(_tempFile, "The brown dog ran.");
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _dataSource.DisposeAsync();
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public async Task IngestThenRecompose_RoundTripsText()
    {
        const string Input = "The brown dog ran.";

        DecomposerConfig config = new()
        {
            SourceDirectory = _tempFile,
            ConnectionString = ConnectionString(),
        };

        TextDecomposer decomposer = new(
            config,
            NullLogger<TextDecomposer>.Instance,
            _cpProps);

        // Run the decomposer through the real pipeline.
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);

        // Find the document entity that was just emitted.
        long docId = await GetMostRecentEntityIdAsync("document");
        Assert.True(docId > 0, "No document entity found after decomposition.");

        // Recompose.
        TextRecomposer recomposer = new(_entityReader);
        string recomposed = await recomposer.RecomposeAsync(
            docId,
            new RecompositionOptions(),
            CancellationToken.None);

        Assert.Equal(Input, recomposed);
    }

    [Fact]
    public async Task ReIngest_SameInput_ProducesNoNewEntities()
    {
        DecomposerConfig config = new()
        {
            SourceDirectory = _tempFile,
            ConnectionString = ConnectionString(),
        };

        TextDecomposer decomposer = new(
            config,
            NullLogger<TextDecomposer>.Instance,
            _cpProps);

        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        long countAfterFirstRun = await GetTotalEntityCountAsync();

        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        long countAfterSecondRun = await GetTotalEntityCountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    private async Task<long> GetMostRecentEntityIdAsync(string entityTypeCode)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.id
              FROM substrate.entity e
              JOIN substrate.entity_type et ON et.id = e.entity_type_id
             WHERE et.code = $1
             ORDER BY e.id DESC
             LIMIT 1
        """;
        cmd.Parameters.Add(new NpgsqlParameter { Value = entityTypeCode });
        object? result = await cmd.ExecuteScalarAsync();
        return result is long id ? id : 0L;
    }

    private async Task<long> GetTotalEntityCountAsync()
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM substrate.entity";
        object? result = await cmd.ExecuteScalarAsync();
        return result is long count ? count : 0L;
    }

    private sealed class NoOpReporter : IProgressReporter
    {
        public Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
    }
}
