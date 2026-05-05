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
    public static RerankResult MapFrom(NpgsqlDataReader reader) =>
        new(
            EntityHash: (byte[])reader.GetValue(0),
            Mu:         reader.GetDouble(1),
            Sigma:      reader.GetDouble(2),
            Games:      reader.GetInt32(3),
            Rank:       reader.GetInt32(4),
            ElapsedMs:  reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
}
