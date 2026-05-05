using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Core.Operations.Results;

/// <summary>
/// Single-row result of <c>substrate.complete(seed_hash, max_depth, max_results, lang_code)</c>.
/// Column order matches the SQL function's RETURNS TABLE declaration:
/// (answer_text, seed_count, distinct_targets, best_target_hash, best_total_mu, elapsed_ms).
/// </summary>
public sealed record CompleteResult(
    string? AnswerText,
    int SeedCount,
    long DistinctTargets,
    byte[]? BestTargetHash,
    double BestTotalMu,
    int ElapsedMs) : IRecordMappable<CompleteResult>
{
    public static CompleteResult MapFrom(NpgsqlDataReader r) =>
        new(
            AnswerText:      r.IsDBNull(0) ? null : r.GetString(0),
            SeedCount:       r.IsDBNull(1) ? 0    : r.GetInt32(1),
            DistinctTargets: r.IsDBNull(2) ? 0L   : r.GetInt64(2),
            BestTargetHash:  r.IsDBNull(3) ? null : (byte[])r.GetValue(3),
            BestTotalMu:     r.IsDBNull(4) ? 0.0  : r.GetDouble(4),
            ElapsedMs:       r.IsDBNull(5) ? 0    : r.GetInt32(5));
}
