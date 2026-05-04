using Hartonomous.Core.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class TextGenerationOp : BaseAiOperation
{
    private const double UserSessionTrustMu = 1000.0;
    private const int DefaultMaxDepth = 3;
    private const int DefaultMaxResults = 25;

    private readonly IPromptIngestion _promptIngestion;

    public TextGenerationOp(
        NpgsqlDataSource dataSource,
        IPromptIngestion promptIngestion,
        ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
        ArgumentNullException.ThrowIfNull(promptIngestion);
        _promptIngestion = promptIngestion;
    }

    public override OperationCode Code => OperationCode.Infer;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        byte[] seedHash = await ResolveSeedHashAsync(request, ct).ConfigureAwait(false);
        int maxDepth = request.MaxDepth ?? DefaultMaxDepth;
        int maxResults = request.MaxResults ?? DefaultMaxResults;

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT answer_text, seed_count, distinct_targets, "
            + "       best_target_hash, best_total_mu, elapsed_ms "
            + "FROM substrate.infer($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(seedHash);
        cmd.Parameters.AddWithValue(maxDepth);
        cmd.Parameters.AddWithValue(maxResults);
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

        Log.InferComplete(Logger, seedCount, distinctTargets, sqlElapsedMs);

        List<ProvenanceTrace> trace = [];
        if (bestHash is not null)
        {
            trace.Add(new ProvenanceTrace(
                EntityHash: bestHash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: null,
                ContributedMu: bestMu,
                OrdinalPosition: 0));
        }

        return new TextGenerationResponse
        {
            OutputCompositionHash = bestHash ?? seedHash,
            OutputModalityCode = "text",
            AnswerText = answer ?? string.Empty,
            NodesVisited = (int)Math.Min(int.MaxValue, distinctTargets),
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["seed_count"] = seedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max_depth"] = maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max_results"] = maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    internal async Task<byte[]> ResolveSeedHashAsync(OperationRequest request, CancellationToken ct)
    {
        if (request.SeedHash is { Length: > 0 })
        {
            return request.SeedHash;
        }
        if (string.IsNullOrEmpty(request.PromptText))
        {
            throw new ArgumentException(
                "TextGenerationOp requires either SeedHash or PromptText.",
                nameof(request));
        }
        return await _promptIngestion.IngestAsync(
            request.PromptText, "user_session", UserSessionTrustMu, ct).ConfigureAwait(false);
    }

    private static TextGenerationResponse EmptyResponse(byte[] seedHash) =>
        new()
        {
            OutputCompositionHash = seedHash,
            OutputModalityCode = "text",
            AnswerText = string.Empty,
            NodesVisited = 0,
            Trace = [],
        };

    private static partial class Log
    {
        [LoggerMessage(EventId = 230, Level = LogLevel.Information,
            Message = "infer seeds={SeedCount} targets={DistinctTargets} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void InferComplete(ILogger logger, int seedCount, long distinctTargets, int sqlElapsedMs);
    }
}

public sealed record TextGenerationResponse : OperationResponse;
