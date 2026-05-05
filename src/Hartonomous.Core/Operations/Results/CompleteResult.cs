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
    public static CompleteResult MapFrom(NpgsqlDataReader reader) =>
        new(
            AnswerText:      reader.IsDBNull(0) ? null : reader.GetString(0),
            SeedCount:       reader.IsDBNull(1) ? 0    : reader.GetInt32(1),
            DistinctTargets: reader.IsDBNull(2) ? 0L   : reader.GetInt64(2),
            BestTargetHash:  reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
            BestTotalMu:     reader.IsDBNull(4) ? 0.0  : reader.GetDouble(4),
            ElapsedMs:       reader.IsDBNull(5) ? 0    : reader.GetInt32(5));
}
