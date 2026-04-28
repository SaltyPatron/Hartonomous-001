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
    /// <summary>
    /// Compile-shim: builds an EntityHandle from a legacy long id. Used to keep
    /// the integration tests building while their helper SQL is migrated to the
    /// composite-key schema (substrate.entity has no .id column anymore; the
    /// helpers below need rewriting to SELECT entity_type_id, hash and return
    /// an EntityHandle directly).
    /// </summary>
    private static Hartonomous.Core.Ingestion.EntityHandle LegacyHandle(long id, string typeCode)
    {
        byte[] h = new byte[32];
        BitConverter.GetBytes(id).CopyTo(h, 0);
        return new Hartonomous.Core.Ingestion.EntityHandle(h, typeCode);
    }

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
        // Embed a GUID so the input is content-unique even when the test runs
        // against a populated substrate that already contains prior fixtures
        // ("The brown dog ran.", Moby Dick, etc.). Content-addressed identity
        // means the document id is deterministic per content, so a unique
        // string guarantees a unique id we can isolate from preceding documents.
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

        // Snapshot pre-decomposition doc-id watermark so the Moby-Dick (or any
        // earlier corpus) document doesn't shadow the one this test creates.
        long preMaxDocId = await GetMostRecentEntityIdAsync("document");

        // Run the decomposer through the real pipeline.
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);

        // Find the document entity emitted by THIS run (id strictly above the
        // pre-decomposition watermark). Unique GUID guarantees a fresh id.
        long docId = await GetMostRecentDocumentAboveAsync(preMaxDocId);
        Assert.True(docId > 0, "No document entity found after decomposition.");

        // Recompose.
        TextRecomposer recomposer = new(_entityReader);
        // INTEGRATION-MIGRATION: docId is a legacy long entity_id; the new schema
        // uses composite (entity_type_id, entity_hash). This test path needs the
        // helper rewritten to SELECT entity_type_id, hash FROM substrate.entity
        // WHERE … and produce an EntityHandle. Compile-shimmed for now.
        Hartonomous.Core.Ingestion.EntityHandle docHandle = LegacyHandle(docId, "document");
        string recomposed = await recomposer.RecomposeAsync(
            docHandle,
            new RecompositionOptions(),
            CancellationToken.None);

        Assert.Equal(Input, recomposed);
    }

    private async Task<long> GetMostRecentDocumentAboveAsync(long minIdExclusive)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.id
              FROM substrate.entity e
              JOIN substrate.entity_type et ON et.id = e.entity_type_id
             WHERE et.code = 'document'
               AND e.id > $1
             ORDER BY e.id DESC
             LIMIT 1
        """;
        cmd.Parameters.Add(new NpgsqlParameter { Value = minIdExclusive });
        object? result = await cmd.ExecuteScalarAsync();
        return result is long id ? id : 0L;
    }

    /// <summary>
    /// Find a document entity whose recomposed text length matches
    /// <paramref name="expectedLength"/>. Used as a fallback when a target
    /// document is already ingested (idempotent re-run yields no new id) and
    /// the watermark pattern can't isolate it. Scans documents in descending
    /// id order so the most-recently-touched matching doc wins.
    /// </summary>
    private async Task<long> FindDocumentByLengthAsync(int expectedLength)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        // Walk documents in descending id order one-at-a-time so a single
        // corrupt fixture (lone-surrogate codepoint left over from a prior
        // session) doesn't poison the whole scan with an encoding error.
        cmd.CommandText = """
            SELECT e.id
              FROM substrate.entity e
              JOIN substrate.entity_type et ON et.id = e.entity_type_id
             WHERE et.code = 'document'
             ORDER BY e.id DESC
        """;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        List<long> docIds = new();
        while (await reader.ReadAsync())
        {
            docIds.Add(reader.GetInt64(0));
        }
        await reader.CloseAsync();

        foreach (long candidateId in docIds)
        {
            try
            {
                await using NpgsqlCommand lenCmd = conn.CreateCommand();
                lenCmd.CommandText = "SELECT char_length(substrate.recompose_text($1))";
                lenCmd.Parameters.Add(new NpgsqlParameter { Value = candidateId });
                object? lenObj = await lenCmd.ExecuteScalarAsync();
                if (lenObj is int len && len == expectedLength)
                {
                    return candidateId;
                }
            }
            catch (PostgresException)
            {
                // Skip documents whose recomposition produces invalid UTF-8.
                // Stale fixtures from prior sessions can contain lone surrogate
                // codepoints; those aren't the document we're looking for.
                continue;
            }
        }
        return 0L;
    }

    [Fact]
    public async Task MobyDick_FullRoundTrip()
    {
        const string Source = @"D:\Models\test_data\text\moby_dick.txt";
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
        long preMaxDocId = await GetMostRecentEntityIdAsync("document");
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        long postCount = await GetTotalEntityCountAsync();

        // Find the Moby-Dick document either as a freshly-emitted id (first-run)
        // or — if it was already in the substrate from a prior run — by the same
        // upper-bound watermark pattern, then fall back to scanning recent docs
        // for the one whose recomposed length matches the source.
        long docId = await GetMostRecentDocumentAboveAsync(preMaxDocId);
        if (docId == 0)
        {
            docId = await FindDocumentByLengthAsync(File.ReadAllText(Source).Length);
        }
        Assert.True(docId > 0, "Moby Dick: no document entity emitted.");

        TextRecomposer recomposer = new(_entityReader);
        Hartonomous.Core.Ingestion.EntityHandle docHandle = LegacyHandle(docId, "document");
        string recomposed = await recomposer.RecomposeAsync(
            docHandle, new RecompositionOptions(), CancellationToken.None);

        string original = await File.ReadAllTextAsync(Source);
        Assert.Equal(original.Length, recomposed.Length);
        Assert.Equal(original, recomposed);

        // Idempotency on the real-world corpus.
        await decomposer.DecomposeAsync(_pipeline, new NoOpReporter(), CancellationToken.None);
        long reCount = await GetTotalEntityCountAsync();
        Assert.Equal(postCount, reCount);

        // Stats — visible in test output.
        Console.WriteLine($"[MobyDick] pre={preCount:N0} post={postCount:N0} delta={postCount - preCount:N0} reingest_delta={reCount - postCount}");
        Console.WriteLine($"[MobyDick] bytes={original.Length:N0} document_id={docId} round_trip=byte_identical");
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
