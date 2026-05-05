using System.Text;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class PromptIngestion : IPromptIngestion
{
    private const int MaxAttempts = 6000;
    private const int PollDelayMs = 50;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IIngestionPipeline _pipeline;
    private readonly ICodepointProperties _codepointProperties;
    private readonly ILogger<PromptIngestion> _logger;

    public PromptIngestion(
        NpgsqlDataSource dataSource,
        IIngestionPipeline pipeline,
        ICodepointProperties codepointProperties,
        ILogger<PromptIngestion> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(codepointProperties);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _pipeline = pipeline;
        _codepointProperties = codepointProperties;
        _logger = logger;
    }

    public async Task<byte[]> IngestAsync(string promptText, string provenanceCode, double trustMu, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(promptText))
        {
            throw new ArgumentException("Prompt text must not be empty.", nameof(promptText));
        }

        IIngestionBatch batch = _pipeline.CreateBatch();
        byte[] utf8 = Encoding.UTF8.GetBytes(promptText);
        TextDecomposeResult ingest = CanonicalTextDecomposer.Emit(
            batch, utf8, _codepointProperties,
            new TextDecomposeOptions(
                ProvenanceCode: provenanceCode,
                TopEntityType: "text_composition",
                TrustMu: trustMu));

        Log.PromptIngested(_logger, promptText.Length, batch.EntityCount);

        await _pipeline.SubmitBatchAsync(batch, ct).ConfigureAwait(false);

        bool drained = await WaitForDocumentAsync(ingest.RootHash, ct).ConfigureAwait(false);
        if (!drained)
        {
            throw new TimeoutException(
                "Prompt did not drain to substrate within 5 minutes. Check pipeline drain task health.");
        }

        return ingest.RootHash;
    }

    private async Task<bool> WaitForDocumentAsync(byte[] hash, CancellationToken ct)
    {
        for (int i = 0; i < MaxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand cmd = new(
                "WITH e AS (SELECT 1 FROM substrate.entity WHERE hash = $1 LIMIT 1), "
                + "     s AS (SELECT 1 FROM substrate.sequence WHERE parent_hash = $1 LIMIT 1) "
                + "SELECT (SELECT count(*) FROM e), (SELECT count(*) FROM s)", conn);
            cmd.Parameters.AddWithValue(hash);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                long entityCount = r.GetInt64(0);
                long sequenceCount = r.GetInt64(1);
                if (entityCount > 0 && sequenceCount > 0)
                {
                    if (i > 0) { Log.DrainBarrier(_logger, i * PollDelayMs); }
                    return true;
                }
            }
            await Task.Delay(PollDelayMs, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 300, Level = LogLevel.Information,
            Message = "prompt ingested {Chars} chars → {Entities} entities")]
        public static partial void PromptIngested(ILogger logger, int chars, long entities);

        [LoggerMessage(EventId = 301, Level = LogLevel.Information,
            Message = "drain barrier crossed in {ElapsedMs}ms")]
        public static partial void DrainBarrier(ILogger logger, int elapsedMs);
    }
}
