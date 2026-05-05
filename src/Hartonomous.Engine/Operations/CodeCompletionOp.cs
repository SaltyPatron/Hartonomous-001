using System.Globalization;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Operations.Results;
using Hartonomous.Engine.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class CodeCompletionOp : BaseAiOperation
{
    private const int DefaultMaxDepth = 4;
    private const int DefaultMaxResults = 25;

    private readonly ISubstrateOpsRepository _repository;
    private readonly IPromptIngestion _promptIngestion;

    public CodeCompletionOp(
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

    public override OperationCode Code => OperationCode.Complete;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding, ModalityLobe.CodeSpecialist];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding, ModalityLobe.CodeSpecialist];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        byte[] seedHash = await ResolveSeedHashAsync(request, ct).ConfigureAwait(false);
        int maxDepth = request.MaxDepth ?? DefaultMaxDepth;
        int maxResults = request.MaxResults ?? DefaultMaxResults;
        string? langCode = request.ExtraOptions?.GetValueOrDefault("lang") ?? request.SourceLanguageCode;

        CompleteResult? result = await _repository
            .CompleteAsync(seedHash, maxDepth, maxResults, langCode, ct)
            .ConfigureAwait(false);

        if (result is null)
        {
            return EmptyResponse(seedHash);
        }

        Log.CompleteDone(Logger, langCode ?? "(any)", result.SeedCount, result.DistinctTargets, result.ElapsedMs);

        List<ProvenanceTrace> trace = [];
        if (result.BestTargetHash is not null)
        {
            trace.Add(new ProvenanceTrace(
                EntityHash: result.BestTargetHash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: langCode,
                ContributedMu: result.BestTotalMu,
                OrdinalPosition: 0));
        }

        return new CodeCompletionResponse
        {
            OutputCompositionHash = result.BestTargetHash ?? seedHash,
            OutputModalityCode = "code",
            AnswerText = result.AnswerText ?? string.Empty,
            NodesVisited = (int)Math.Min(int.MaxValue, result.DistinctTargets),
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = result.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                ["seed_count"] = result.SeedCount.ToString(CultureInfo.InvariantCulture),
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
