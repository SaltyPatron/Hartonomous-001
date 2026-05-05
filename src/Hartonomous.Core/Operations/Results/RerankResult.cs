using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Core.Operations.Results;

/// <summary>
/// Per-row result of <c>substrate.rerank(seed_hash, candidate_hashes, k)</c>.
/// One row per ranked candidate, ordered ascending by rank. Column shape:
/// (entity_hash, mu, sigma, games, rank, elapsed_ms).
/// </summary>
public sealed record RerankResult(
    byte[] EntityHash,
    double Mu,
    double Sigma,
    int Games,
    int Rank,
    int ElapsedMs) : IRecordMappable<RerankResult>
{
    public static RerankResult MapFrom(NpgsqlDataReader r) =>
        new(
            EntityHash: (byte[])r.GetValue(0),
            Mu:         r.GetDouble(1),
            Sigma:      r.GetDouble(2),
            Games:      r.GetInt32(3),
            Rank:       r.GetInt32(4),
            ElapsedMs:  r.IsDBNull(5) ? 0 : r.GetInt32(5));
}
