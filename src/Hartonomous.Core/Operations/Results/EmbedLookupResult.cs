using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Core.Operations.Results;

/// <summary>
/// Per-row result of <c>substrate.embed_lookup(seed_hash, entity_type_code, k, distance_kind, threshold)</c>.
/// One row per neighbor, ordered ascending by distance. Column shape:
/// (entity_type_id, entity_hash, distance, elapsed_ms).
/// </summary>
public sealed record EmbedLookupResult(
    int EntityTypeId,
    byte[] EntityHash,
    double Distance,
    int ElapsedMs) : IRecordMappable<EmbedLookupResult>
{
    public static EmbedLookupResult MapFrom(NpgsqlDataReader r) =>
        new(
            EntityTypeId: r.GetInt32(0),
            EntityHash:   (byte[])r.GetValue(1),
            Distance:     r.GetDouble(2),
            ElapsedMs:    r.IsDBNull(3) ? 0 : r.GetInt32(3));
}
