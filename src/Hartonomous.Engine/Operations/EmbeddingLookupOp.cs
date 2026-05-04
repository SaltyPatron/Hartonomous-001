using Hartonomous.Core.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class EmbeddingLookupOp : BaseAiOperation
{
    private const int DefaultK = 10;
    private const string DefaultDistanceKind = "4d";

    public EmbeddingLookupOp(NpgsqlDataSource dataSource, ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
    }

    public override OperationCode Code => OperationCode.EmbedLookup;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        if (request.SeedHash is null || request.SeedHash.Length == 0)
        {
            throw new ArgumentException(
                "EmbeddingLookupOp requires SeedHash. Prompt-decompose-and-drain wiring lands in CK-23; until then, supply a seed entity hash directly.",
                nameof(request));
        }

        string entityTypeCode = request.ExtraOptions?.GetValueOrDefault("entity_type")
            ?? throw new ArgumentException(
                "EmbeddingLookupOp requires ExtraOptions['entity_type'] (e.g. 'lemma', 'word_form', 'tensor').",
                nameof(request));

        int k = request.MaxResults ?? DefaultK;
        string distanceKind = request.ExtraOptions?.GetValueOrDefault("distance_kind") ?? DefaultDistanceKind;
        double? threshold = ParseThreshold(request.ExtraOptions);

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_type_id, entity_hash, distance, elapsed_ms "
            + "FROM substrate.embed_lookup($1, $2, $3, $4, $5)",
            conn);
        cmd.Parameters.AddWithValue(request.SeedHash);
        cmd.Parameters.AddWithValue(entityTypeCode);
        cmd.Parameters.AddWithValue(k);
        cmd.Parameters.AddWithValue(distanceKind);
        if (threshold.HasValue)
        {
            cmd.Parameters.AddWithValue(threshold.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue(DBNull.Value);
        }

        List<ProvenanceTrace> trace = [];
        int sqlElapsedMs = 0;
        byte[]? bestHash = null;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int entityTypeId = r.GetInt32(0);
            byte[] hash = (byte[])r.GetValue(1);
            double distance = r.GetDouble(2);
            sqlElapsedMs = r.GetInt32(3);

            bestHash ??= hash;
            trace.Add(new ProvenanceTrace(
                EntityHash: hash,
                EntityTypeId: entityTypeId,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: null,
                ContributedMu: distance,
                OrdinalPosition: trace.Count));
        }

        Log.EmbedLookupComplete(Logger, trace.Count, distanceKind, sqlElapsedMs);

        return new EmbeddingLookupResponse
        {
            OutputCompositionHash = bestHash ?? request.SeedHash,
            OutputModalityCode = "text",
            AnswerText = null,
            NodesVisited = trace.Count,
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["distance_kind"] = distanceKind,
                ["entity_type"] = entityTypeCode,
            },
        };
    }

    private static double? ParseThreshold(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) { return null; }
        if (!options.TryGetValue("distance_threshold", out string? v) || string.IsNullOrEmpty(v)) { return null; }
        return double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Information,
            Message = "embed_lookup hits={HitCount} kind={DistanceKind} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void EmbedLookupComplete(ILogger logger, int hitCount, string distanceKind, int sqlElapsedMs);
    }
}

public sealed record EmbeddingLookupResponse : OperationResponse;
