using System.Globalization;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Operations.Results;
using Hartonomous.Engine.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class TextGenerationOp : BaseAiOperation
{
    private const double UserSessionTrustMu = 1000.0;
    private const int DefaultMaxDepth = 3;
    private const int DefaultMaxResults = 25;

    private readonly ISubstrateOpsRepository _repository;
    private readonly IPromptIngestion _promptIngestion;

    public TextGenerationOp(
        NpgsqlDataSource dataSource,
        ISubstrateOpsRepository repository,
        IPromptIngestion promptIngestion,
        ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(promptIngestion);
        _repository = repository;
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

        InferResult? result = await _repository
            .InferAsync(seedHash, maxDepth, maxResults, ct)
            .ConfigureAwait(false);

        if (result is null)
        {
            return EmptyResponse(seedHash);
        }

        Log.InferComplete(Logger, result.SeedCount, result.DistinctTargets, result.ElapsedMs);

        List<ProvenanceTrace> trace = [];
        if (result.BestTargetHash is not null)
        {
            trace.Add(new ProvenanceTrace(
                EntityHash: result.BestTargetHash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: null,
                ContributedMu: result.BestTotalMu,
                OrdinalPosition: 0));
        }

        return new TextGenerationResponse
        {
            OutputCompositionHash = result.BestTargetHash ?? seedHash,
            OutputModalityCode = "text",
            AnswerText = result.AnswerText ?? string.Empty,
            NodesVisited = (int)Math.Min(int.MaxValue, result.DistinctTargets),
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = result.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                ["seed_count"] = result.SeedCount.ToString(CultureInfo.InvariantCulture),
                ["max_depth"] = maxDepth.ToString(CultureInfo.InvariantCulture),
                ["max_results"] = maxResults.ToString(CultureInfo.InvariantCulture),
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
