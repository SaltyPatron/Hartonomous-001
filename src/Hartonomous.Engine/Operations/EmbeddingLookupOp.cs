using System.Globalization;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Operations.Results;
using Hartonomous.Engine.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class EmbeddingLookupOp : BaseAiOperation
{
    private const int DefaultK = 10;
    private const string DefaultDistanceKind = "4d";
    private const double UserSessionTrustMu = 1000.0;

    private readonly ISubstrateOpsRepository _repository;
    private readonly IPromptIngestion _promptIngestion;

    public EmbeddingLookupOp(
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

    public override OperationCode Code => OperationCode.EmbedLookup;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        byte[] seedHash = await ResolveSeedHashAsync(request, ct).ConfigureAwait(false);

        string entityTypeCode = request.ExtraOptions?.GetValueOrDefault("entity_type")
            ?? throw new ArgumentException(
                "EmbeddingLookupOp requires ExtraOptions['entity_type'] (e.g. 'lemma', 'word_form', 'tensor').",
                nameof(request));

        int k = request.MaxResults ?? DefaultK;
        string distanceKind = request.ExtraOptions?.GetValueOrDefault("distance_kind") ?? DefaultDistanceKind;
        double? threshold = ParseThreshold(request.ExtraOptions);

        IReadOnlyList<EmbedLookupResult> rows = await _repository
            .EmbedLookupAsync(seedHash, entityTypeCode, k, distanceKind, threshold, ct)
            .ConfigureAwait(false);

        List<ProvenanceTrace> trace = new(rows.Count);
        int sqlElapsedMs = 0;
        byte[]? bestHash = null;
        for (int i = 0; i < rows.Count; i++)
        {
            EmbedLookupResult row = rows[i];
            sqlElapsedMs = row.ElapsedMs;
            bestHash ??= row.EntityHash;
            trace.Add(new ProvenanceTrace(
                EntityHash: row.EntityHash,
                EntityTypeId: row.EntityTypeId,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: null,
                ContributedMu: row.Distance,
                OrdinalPosition: i));
        }

        Log.EmbedLookupComplete(Logger, rows.Count, distanceKind, sqlElapsedMs);

        return new EmbeddingLookupResponse
        {
            OutputCompositionHash = bestHash ?? seedHash,
            OutputModalityCode = "text",
            AnswerText = null,
            NodesVisited = rows.Count,
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(CultureInfo.InvariantCulture),
                ["distance_kind"] = distanceKind,
                ["entity_type"] = entityTypeCode,
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
                "EmbeddingLookupOp requires either SeedHash or PromptText.",
                nameof(request));
        }
        return await _promptIngestion.IngestAsync(
            request.PromptText, "user_session", UserSessionTrustMu, ct).ConfigureAwait(false);
    }

    private static double? ParseThreshold(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) { return null; }
        if (!options.TryGetValue("distance_threshold", out string? v) || string.IsNullOrEmpty(v)) { return null; }
        return double.TryParse(v, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Information,
            Message = "embed_lookup hits={HitCount} kind={DistanceKind} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void EmbedLookupComplete(ILogger logger, int hitCount, string distanceKind, int sqlElapsedMs);
    }
}
