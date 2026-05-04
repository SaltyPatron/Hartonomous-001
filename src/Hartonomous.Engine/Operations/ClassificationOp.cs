using Hartonomous.Core.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class ClassificationOp : BaseAiOperation
{
    private const string DefaultJunctionKind = "pos";
    private const int DefaultK = 10;

    public ClassificationOp(NpgsqlDataSource dataSource, ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
    }

    public override OperationCode Code => OperationCode.Classify;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        if (request.SeedHash is null || request.SeedHash.Length == 0)
        {
            throw new ArgumentException(
                "ClassificationOp requires SeedHash. Prompt-decompose-and-drain wiring lands in CK-23.",
                nameof(request));
        }

        string junctionKind = request.ExtraOptions?.GetValueOrDefault("junction_kind") ?? DefaultJunctionKind;
        int k = request.MaxResults ?? DefaultK;

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT label_id, label_code, mu, sigma, games, elapsed_ms "
            + "FROM substrate.classify($1, $2, $3)",
            conn);
        cmd.Parameters.AddWithValue(request.SeedHash);
        cmd.Parameters.AddWithValue(junctionKind);
        cmd.Parameters.AddWithValue(k);

        List<ProvenanceTrace> trace = [];
        Dictionary<string, string> labelLookup = new(StringComparer.Ordinal);
        int sqlElapsedMs = 0;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int labelId = r.GetInt32(0);
            string labelCode = r.GetString(1);
            double? mu = r.IsDBNull(2) ? null : r.GetDouble(2);
            sqlElapsedMs = r.GetInt32(5);

            trace.Add(new ProvenanceTrace(
                EntityHash: request.SeedHash,
                EntityTypeId: labelId,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: labelCode,
                ContributedMu: mu,
                OrdinalPosition: trace.Count));

            labelLookup[$"label_{trace.Count}"] = labelCode;
        }

        Log.ClassifyComplete(Logger, junctionKind, trace.Count, sqlElapsedMs);

        Dictionary<string, string> diagnostics = new(StringComparer.Ordinal)
        {
            ["sql_elapsed_ms"] = sqlElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["junction_kind"] = junctionKind,
        };
        foreach (KeyValuePair<string, string> kvp in labelLookup)
        {
            diagnostics[kvp.Key] = kvp.Value;
        }

        return new ClassificationResponse
        {
            OutputCompositionHash = request.SeedHash,
            OutputModalityCode = "text",
            AnswerText = trace.Count > 0 ? trace[0].ProvenanceCode : null,
            NodesVisited = trace.Count,
            Trace = trace,
            ExtraDiagnostics = diagnostics,
        };
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 210, Level = LogLevel.Information,
            Message = "classify kind={JunctionKind} hits={HitCount} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void ClassifyComplete(ILogger logger, string junctionKind, int hitCount, int sqlElapsedMs);
    }
}

public sealed record ClassificationResponse : OperationResponse;
