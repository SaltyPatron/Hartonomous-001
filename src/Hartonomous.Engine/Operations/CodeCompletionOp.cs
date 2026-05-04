using System.Globalization;
using Hartonomous.Core.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class CodeCompletionOp : BaseAiOperation
{
    private const int DefaultMaxDepth = 4;
    private const int DefaultMaxResults = 25;

    private readonly IPromptIngestion _promptIngestion;

    public CodeCompletionOp(
        NpgsqlDataSource dataSource,
        IPromptIngestion promptIngestion,
        ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
        ArgumentNullException.ThrowIfNull(promptIngestion);
        _promptIngestion = promptIngestion;
    }

    public override OperationCode Code => OperationCode.Complete;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding, ModalityLobe.CodeSpecialist];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding, ModalityLobe.CodeSpecialist];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        byte[] seedHash = await ResolveSeedHashAsync(request, ct).ConfigureAwait(false);
        int maxDepth = request.MaxDepth ?? DefaultMaxDepth;
        int maxResults = request.MaxResults ?? DefaultMaxResults;
        string? langCode = request.ExtraOptions?.GetValueOrDefault("lang") ?? request.SourceLanguageCode;

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT answer_text, seed_count, distinct_targets, "
            + "       best_target_hash, best_total_mu, elapsed_ms "
            + "FROM substrate.complete($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(seedHash);
        cmd.Parameters.AddWithValue(maxDepth);
        cmd.Parameters.AddWithValue(maxResults);
        cmd.Parameters.AddWithValue((object?)langCode ?? DBNull.Value);
        cmd.CommandTimeout = 300;

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return EmptyResponse(seedHash);
        }
        string? answer = r.IsDBNull(0) ? null : r.GetString(0);
        int seedCount = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        long distinctTargets = r.IsDBNull(2) ? 0L : r.GetInt64(2);
        byte[]? bestHash = r.IsDBNull(3) ? null : (byte[])r.GetValue(3);
        double bestMu = r.IsDBNull(4) ? 0.0 : r.GetDouble(4);
        int sqlElapsedMs = r.IsDBNull(5) ? 0 : r.GetInt32(5);

        Log.CompleteDone(Logger, langCode ?? "(any)", seedCount, distinctTargets, sqlElapsedMs);

        List<ProvenanceTrace> trace = [];
        if (bestHash is not null)
        {
            trace.Add(new ProvenanceTrace(
                EntityHash: bestHash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: langCode,
                ContributedMu: bestMu,
                OrdinalPosition: 0));
        }

        return new CodeCompletionResponse
        {
            OutputCompositionHash = bestHash ?? seedHash,
            OutputModalityCode = "code",
            AnswerText = answer ?? string.Empty,
            NodesVisited = (int)Math.Min(int.MaxValue, distinctTargets),
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(CultureInfo.InvariantCulture),
                ["seed_count"] = seedCount.ToString(CultureInfo.InvariantCulture),
                ["lang"] = langCode ?? string.Empty,
            },
        };
    }

    private async Task<byte[]> ResolveSeedHashAsync(OperationRequest request, CancellationToken ct)
    {
        if (request.SeedHash is { Length: > 0 })
        {
            return request.SeedHash;
        }
        if (string.IsNullOrEmpty(request.PromptText))
        {
            throw new ArgumentException(
                "CodeCompletionOp requires either SeedHash or PromptText.",
                nameof(request));
        }
        return await _promptIngestion.IngestAsync(
            request.PromptText, "user_session", 1000.0, ct).ConfigureAwait(false);
    }

    private static CodeCompletionResponse EmptyResponse(byte[] seedHash) =>
        new()
        {
            OutputCompositionHash = seedHash,
            OutputModalityCode = "code",
            AnswerText = string.Empty,
            NodesVisited = 0,
            Trace = [],
        };

    private static partial class Log
    {
        [LoggerMessage(EventId = 240, Level = LogLevel.Information,
            Message = "complete lang={Lang} seeds={Seeds} targets={DistinctTargets} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void CompleteDone(ILogger logger, string lang, int seeds, long distinctTargets, int sqlElapsedMs);
    }
}

public sealed record CodeCompletionResponse : OperationResponse;
