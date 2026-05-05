using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Core.Operations.Results;

/// <summary>
/// Single-row result of <c>substrate.infer(seed_hash, max_depth, max_results)</c>.
/// Same column shape as <see cref="CompleteResult"/> (the two SQL functions
/// share a RETURNS TABLE signature) but kept as a distinct record for type
/// safety at the C# call site.
/// </summary>
public sealed record InferResult(
    string? AnswerText,
    int SeedCount,
    long DistinctTargets,
    byte[]? BestTargetHash,
    double BestTotalMu,
    int ElapsedMs) : IRecordMappable<InferResult>
{
    public static InferResult MapFrom(NpgsqlDataReader r) =>
        new(
            AnswerText:      r.IsDBNull(0) ? null : r.GetString(0),
            SeedCount:       r.IsDBNull(1) ? 0    : r.GetInt32(1),
            DistinctTargets: r.IsDBNull(2) ? 0L   : r.GetInt64(2),
            BestTargetHash:  r.IsDBNull(3) ? null : (byte[])r.GetValue(3),
            BestTotalMu:     r.IsDBNull(4) ? 0.0  : r.GetDouble(4),
            ElapsedMs:       r.IsDBNull(5) ? 0    : r.GetInt32(5));
}
