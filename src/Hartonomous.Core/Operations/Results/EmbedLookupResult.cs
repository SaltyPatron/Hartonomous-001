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
    public static EmbedLookupResult MapFrom(NpgsqlDataReader reader) =>
        new(
            EntityTypeId: reader.GetInt32(0),
            EntityHash:   (byte[])reader.GetValue(1),
            Distance:     reader.GetDouble(2),
            ElapsedMs:    reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
}
