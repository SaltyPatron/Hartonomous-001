using Hartonomous.Core.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Operations;

public sealed partial class RerankingOp : BaseAiOperation
{
    private const int DefaultK = 25;

    public RerankingOp(NpgsqlDataSource dataSource, ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
    }

    public override OperationCode Code => OperationCode.Rerank;

    public override ModalityLobe[] InputLobes => [ModalityLobe.TextEmbedding];

    public override ModalityLobe[] OutputLobes => [ModalityLobe.TextEmbedding];

    protected override async Task<OperationResponse> ExecuteCoreAsync(OperationRequest request, CancellationToken ct)
    {
        if (request is not RerankingRequest rr)
        {
            throw new ArgumentException(
                "RerankingOp requires a RerankingRequest with non-empty Candidates.",
                nameof(request));
        }
        if (rr.Candidates is null || rr.Candidates.Count == 0)
        {
            throw new ArgumentException("Candidates must contain at least one hash.", nameof(request));
        }
        if (string.IsNullOrEmpty(rr.ArenaCode))
        {
            throw new ArgumentException("ArenaCode is required.", nameof(request));
        }

        int k = request.MaxResults ?? DefaultK;

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_hash, mu, sigma, games, rank, elapsed_ms "
            + "FROM substrate.rerank($1, $2, $3)",
            conn);
        NpgsqlParameter candParam = new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = rr.Candidates.ToArray() };
        cmd.Parameters.Add(candParam);
        cmd.Parameters.AddWithValue(rr.ArenaCode);
        cmd.Parameters.AddWithValue(k);

        List<ProvenanceTrace> trace = [];
        int sqlElapsedMs = 0;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] hash = (byte[])r.GetValue(0);
            double mu = r.GetDouble(1);
            int rank = r.GetInt32(4);
            sqlElapsedMs = r.GetInt32(5);

            trace.Add(new ProvenanceTrace(
                EntityHash: hash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: rr.ArenaCode,
                ContributedMu: mu,
                OrdinalPosition: rank));
        }

        Log.RerankComplete(Logger, rr.ArenaCode, rr.Candidates.Count, trace.Count, sqlElapsedMs);

        byte[] best = trace.Count > 0 ? trace[0].EntityHash : rr.Candidates[0];
        return new RerankingResponse
        {
            OutputCompositionHash = best,
            OutputModalityCode = "text",
            AnswerText = null,
            NodesVisited = trace.Count,
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["arena"] = rr.ArenaCode,
                ["candidate_count"] = rr.Candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 220, Level = LogLevel.Information,
            Message = "rerank arena={Arena} candidates={CandidateCount} returned={ReturnedCount} sql_elapsed={SqlElapsedMs}ms")]
        public static partial void RerankComplete(ILogger logger, string arena, int candidateCount, int returnedCount, int sqlElapsedMs);
    }
}

public sealed record RerankingRequest : OperationRequest
{
    public required IReadOnlyList<byte[]> Candidates { get; init; }
}

public sealed record RerankingResponse : OperationResponse;
