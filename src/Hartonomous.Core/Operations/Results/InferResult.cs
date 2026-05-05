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
    public static InferResult MapFrom(NpgsqlDataReader reader) =>
        new(
            AnswerText:      reader.IsDBNull(0) ? null : reader.GetString(0),
            SeedCount:       reader.IsDBNull(1) ? 0    : reader.GetInt32(1),
            DistinctTargets: reader.IsDBNull(2) ? 0L   : reader.GetInt64(2),
            BestTargetHash:  reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
            BestTotalMu:     reader.IsDBNull(4) ? 0.0  : reader.GetDouble(4),
            ElapsedMs:       reader.IsDBNull(5) ? 0    : reader.GetInt32(5));
}
