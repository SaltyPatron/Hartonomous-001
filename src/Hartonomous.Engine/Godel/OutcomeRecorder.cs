using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// Step 6 of inference.md: arena update. When the user (or a downstream
/// task) signals that an inference outcome was correct or incorrect, the
/// engine emits Glicko-2 comparison events on the substrate edges that
/// supported the selected vs rejected alternatives. Mu rises on the
/// winner's edges; mu falls on each loser's edges; sigma tightens with
/// every comparison. The substrate learns from interaction without a
/// gradient.
///
/// Calls substrate.record_outcomes_bulk once per response. SQL fans the
/// flattened outcome groups across every row in substrate.significance_context
/// (open-vocabulary, per AP-1), then routes each group through the set-based
/// native-Glicko substrate.record_outcome implementation.
/// </summary>
public sealed partial class OutcomeRecorder
{
    private const string AcceptAttestationTypeCode = "positive_evidence";
    private const string RejectAttestationTypeCode = "negative_evidence";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<OutcomeRecorder> _logger;

    public OutcomeRecorder(NpgsqlDataSource dataSource, ILogger<OutcomeRecorder> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Record an "accept" outcome: the primary candidate of each
    /// sub-question is the winner; the remaining top-K are losers. Updates
    /// fire across every arena so the substrate's significance ratings
    /// converge across all relevant contexts.
    /// </summary>
    public Task RecordAcceptAsync(GodelResponse response, CancellationToken ct) =>
        RecordOutcomeAsync(response, accept: true, ct);

    /// <summary>
    /// Record a "reject" outcome: the primary candidate becomes the loser
    /// against the remaining alternatives. Inverts the accept update so
    /// the next inference's significance reflects user feedback.
    /// </summary>
    public Task RecordRejectAsync(GodelResponse response, CancellationToken ct) =>
        RecordOutcomeAsync(response, accept: false, ct);

    private async Task RecordOutcomeAsync(GodelResponse response, bool accept, CancellationToken ct)
    {
        if (response.SubQuestionResults.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        List<byte[]> winnerHashes = [];
        List<int> winnerGroupIds = [];
        List<byte[]> loserHashes = [];
        List<int> loserGroupIds = [];
        int groupId = 0;
        foreach (SubQuestionResult sq in response.SubQuestionResults)
        {
            if (sq.Candidates.Count < 2)
            {
                // Need at least one alternative for a comparison.
                continue;
            }

            byte[] winner = accept ? sq.Candidates[0].TargetHash : sq.Candidates[^1].TargetHash;
            List<byte[]> losers = new(sq.Candidates.Count - 1);
            foreach (GodelCandidate c in sq.Candidates)
            {
                if (c.TargetHash != winner)
                {
                    losers.Add(c.TargetHash);
                }
            }
            if (losers.Count == 0)
            {
                continue;
            }

            winnerHashes.Add(winner);
            winnerGroupIds.Add(groupId);
            foreach (byte[] loser in losers)
            {
                loserHashes.Add(loser);
                loserGroupIds.Add(groupId);
            }
            groupId++;
        }

        if (winnerHashes.Count == 0 || loserHashes.Count == 0)
        {
            return;
        }

        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.RecordOutcomesBulk,
            [
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = winnerHashes.ToArray() },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = winnerGroupIds.ToArray() },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = loserHashes.ToArray() },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = loserGroupIds.ToArray() },
                new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Text,
                    Value = accept ? AcceptAttestationTypeCode : RejectAttestationTypeCode,
                },
            ]);
        object? raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        int totalEvents = raw is int i ? i : 0;
        LogOutcomeRecorded(_logger, accept ? "accept" : "reject", totalEvents, winnerHashes.Count);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OutcomeRecorder: {Outcome} → {Events} comparison events across {Groups} outcome groups")]
    private static partial void LogOutcomeRecorded(ILogger logger, string outcome, int events, int groups);
}
