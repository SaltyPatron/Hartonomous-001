using System.Globalization;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Operations.Results;
using Hartonomous.Engine.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class ClassificationOp : BaseAiOperation
{
    private const string DefaultJunctionKind = "pos";
    private const int DefaultK = 10;
    private const double UserSessionTrustMu = 1000.0;

    private readonly ISubstrateOpsRepository _repository;
    private readonly IPromptIngestion _promptIngestion;

    public ClassificationOp(
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

    public override OperationCode Code => OperationCode.Classify;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        byte[] seedHash = await ResolveSeedHashAsync(request, ct).ConfigureAwait(false);
        string junctionKind = request.ExtraOptions?.GetValueOrDefault("junction_kind") ?? DefaultJunctionKind;
        int k = request.MaxResults ?? DefaultK;

        IReadOnlyList<ClassifyResult> rows = await _repository
            .ClassifyAsync(seedHash, junctionKind, k, ct)
            .ConfigureAwait(false);

        List<ProvenanceTrace> trace = new(rows.Count);
        Dictionary<string, string> labelLookup = new(StringComparer.Ordinal);
        int sqlElapsedMs = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            ClassifyResult row = rows[i];
            sqlElapsedMs = row.ElapsedMs;
            trace.Add(new ProvenanceTrace(
                EntityHash: seedHash,
                EntityTypeId: row.LabelId,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: row.LabelCode,
                ContributedMu: row.Mu,
                OrdinalPosition: i));
            labelLookup[$"label_{i + 1}"] = row.LabelCode;
        }

        Log.ClassifyComplete(Logger, junctionKind, rows.Count, sqlElapsedMs);

        Dictionary<string, string> diagnostics = new(StringComparer.Ordinal)
        {
            ["sql_elapsed_ms"] = sqlElapsedMs.ToString(CultureInfo.InvariantCulture),
            ["junction_kind"] = junctionKind,
        };
        foreach (KeyValuePair<string, string> kvp in labelLookup)
        {
            diagnostics[kvp.Key] = kvp.Value;
        }

        return new ClassificationResponse
        {
            OutputCompositionHash = seedHash,
            OutputModalityCode = "text",
            AnswerText = trace.Count > 0 ? trace[0].ProvenanceCode : null,
            NodesVisited = trace.Count,
            Trace = trace,
            ExtraDiagnostics = diagnostics,
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
                "ClassificationOp requires either SeedHash or PromptText.",
                nameof(request));
        }
        return await _promptIngestion.IngestAsync(
            request.PromptText, "user_session", UserSessionTrustMu, ct).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 210, Level = LogLevel.Information,
            Message = "classify kind={JunctionKind} hits={HitCount} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void ClassifyComplete(ILogger logger, string junctionKind, int hitCount, int sqlElapsedMs);
    }
}

public sealed record ClassificationResponse : OperationResponse;
