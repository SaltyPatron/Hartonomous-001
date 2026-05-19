using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Decomposers.Text;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Text;
using Hartonomous.Recomposers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// End-to-end vertical slice: write a text file, run TextDecomposer through
/// StreamingIngestionPipeline, recompose the document via TextRecomposer, assert
/// the recomposed bytes equal the input, then re-run and assert no new
/// entities are created (Law #6 idempotency).
///
/// Hash-as-PK throughout. Captures the composite document handle directly
/// from <see cref="TextDecomposer.LastDocumentHandle"/> rather than scanning
/// substrate.entity for a surrogate "most recent id" (which doesn't exist
/// in the post-0006 schema).
///
/// Requires a running PostgreSQL with migrations applied through 0015 and
/// at minimum the UCD codepoint property seed loaded.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001",
    Justification = "Disposable fields are released in IAsyncLifetime.DisposeAsync.")]
public sealed class TextRoundTripTests : IAsyncLifetime
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=/var/run/postgresql;Username=ahart;Database=hartonomous";

    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlReferenceDataReader _refReader = null!;
    private ContentRecomposer _recomposer = null!;
    private StreamingIngestionPipeline _pipeline = null!;
    private NpgsqlCodepointPropertiesCache _cpProps = null!;
    private string _tempFile = null!;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(ConnectionString());
        _refReader = new NpgsqlReferenceDataReader(_dataSource);
        _recomposer = new ContentRecomposer(_dataSource, NullLogger<ContentRecomposer>.Instance);
        _pipeline = new StreamingIngestionPipeline(
            ConnectionString(),
            _refReader,
            NullLogger<StreamingIngestionPipeline>.Instance);
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
        // Embed a GUID so the input is content-unique even when the test runs
        // against a populated substrate. Content-addressed identity means the
        // document hash is deterministic per content; the GUID guarantees a
        // hash that hasn't been seen before, so dedup behaviour is observable.
        string Input = $"The brown dog ran. id={Guid.NewGuid():N}";
        await File.WriteAllTextAsync(_tempFile, Input);

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
        await _pipeline.DrainPendingAsync(CancellationToken.None);

        EntityHandle? docHandle = decomposer.LastDocumentHandle;
        Assert.NotNull(docHandle);

        // Recompose via the C# bulk-tier ContentRecomposer (Gate 1 item #36).
        byte[] recomposedBytes = await _recomposer.RecomposeAsync(
            docHandle.Value.Hash,
            maxDepth: 16,
            CancellationToken.None);
        string recomposed = Encoding.UTF8.GetString(recomposedBytes);

        Assert.Equal(Input, recomposed);
    }

    [Fact]
    public async Task MobyDick_FullRoundTrip()
    {
        const string Source = "/vault/Data/test_data/text/moby_dick.txt";
        if (!File.Exists(Source))
        {
            return; // skip silently if the corpus isn't present
        }

        DecomposerConfig config = new()
        {
            SourceDirectory = Source,
            ConnectionString = ConnectionString(),
        };

        TextDecomposer decomposer = new(
            config,
            NullLogger<TextDecomposer>.Instance,
            _cpProps);

        long preCount = await GetTotalEntityCountAsync();
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        await _pipeline.DrainPendingAsync(CancellationToken.None);
        long postCount = await GetTotalEntityCountAsync();

        EntityHandle? docHandle = decomposer.LastDocumentHandle;
        Assert.NotNull(docHandle);

        Stopwatch sw = Stopwatch.StartNew();
        byte[] recomposedBytes = await _recomposer.RecomposeAsync(
            docHandle.Value.Hash, maxDepth: 32, CancellationToken.None);
        sw.Stop();
        string recomposed = Encoding.UTF8.GetString(recomposedBytes);

        string original = await File.ReadAllTextAsync(Source);
        Console.WriteLine($"[MobyDick] recompose elapsed={sw.ElapsedMilliseconds}ms for {original.Length:N0} bytes");
        Assert.True(
            sw.ElapsedMilliseconds < 1000,
            $"Moby Dick recompose took {sw.ElapsedMilliseconds}ms; budget is 1000ms.");
        Assert.Equal(original.Length, recomposed.Length);
        Assert.Equal(original, recomposed);

        // Idempotency on the real-world corpus.
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        await _pipeline.DrainPendingAsync(CancellationToken.None);
        long reCount = await GetTotalEntityCountAsync();
        Assert.Equal(postCount, reCount);

        Console.WriteLine($"[MobyDick] pre={preCount:N0} post={postCount:N0} delta={postCount - preCount:N0} reingest_delta={reCount - postCount}");
        Console.WriteLine($"[MobyDick] bytes={original.Length:N0} document={docHandle.Value} round_trip=byte_identical");
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
        await _pipeline.DrainPendingAsync(CancellationToken.None);
        long countAfterFirstRun = await GetTotalEntityCountAsync();

        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        await _pipeline.DrainPendingAsync(CancellationToken.None);
        long countAfterSecondRun = await GetTotalEntityCountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
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
