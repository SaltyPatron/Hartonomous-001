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
/// Calls substrate.record_outcome(arena_id, winner, losers[]) per arena.
/// The default arena set is every row in substrate.significance_context
/// (open-vocabulary, per AP-1).
/// </summary>
public sealed partial class OutcomeRecorder
{
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

        // Load arenas — open-vocabulary, no hardcoded list.
        List<int> arenaIds = new();
        await using (NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.SignificanceContextIds))
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                arenaIds.Add(reader.GetInt32(0));
            }
        }

        int totalEvents = 0;
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
            byte[][] losersArr = losers.ToArray();

            foreach (int arenaId in arenaIds)
            {
                await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                    conn,
                    SubstrateFunctionNames.RecordOutcome,
                    new NpgsqlParameter[]
                    {
                        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = arenaId },
                        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = winner },
                        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = losersArr },
                    });
                object? raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                int events = raw is int i ? i : 0;
                totalEvents += events;
            }
        }
        LogOutcomeRecorded(_logger, accept ? "accept" : "reject", totalEvents, arenaIds.Count);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OutcomeRecorder: {Outcome} → {Events} comparison events across {Arenas} arenas")]
    private static partial void LogOutcomeRecorded(ILogger logger, string outcome, int events, int arenas);
}
