using System.Globalization;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Operations.Results;
using Hartonomous.Engine.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Operations;

public sealed partial class RerankingOp : BaseAiOperation
{
    private const int DefaultK = 25;

    private readonly ISubstrateOpsRepository _repository;

    public RerankingOp(
        NpgsqlDataSource dataSource,
        ISubstrateOpsRepository repository,
        ILogger<BaseAiOperation> logger)
        : base(dataSource, logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
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
        byte[][] candidates = rr.Candidates is byte[][] arr ? arr : [.. rr.Candidates];

        IReadOnlyList<RerankResult> rows = await _repository
            .RerankAsync(candidates, rr.ArenaCode, k, ct)
            .ConfigureAwait(false);

        List<ProvenanceTrace> trace = new(rows.Count);
        int sqlElapsedMs = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            RerankResult row = rows[i];
            sqlElapsedMs = row.ElapsedMs;
            trace.Add(new ProvenanceTrace(
                EntityHash: row.EntityHash,
                EntityTypeId: null,
                EdgeHash: null,
                EdgeTypeId: null,
                ProvenanceCode: rr.ArenaCode,
                ContributedMu: row.Mu,
                OrdinalPosition: row.Rank));
        }

        Log.RerankComplete(Logger, rr.ArenaCode, rr.Candidates.Count, rows.Count, sqlElapsedMs);

        byte[] best = trace.Count > 0 ? trace[0].EntityHash : rr.Candidates[0];
        return new RerankingResponse
        {
            OutputCompositionHash = best,
            OutputModalityCode = "text",
            AnswerText = null,
            NodesVisited = rows.Count,
            Trace = trace,
            ExtraDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sql_elapsed_ms"] = sqlElapsedMs.ToString(CultureInfo.InvariantCulture),
                ["arena"] = rr.ArenaCode,
                ["candidate_count"] = rr.Candidates.Count.ToString(CultureInfo.InvariantCulture),
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
