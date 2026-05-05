using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Core.Operations.Results;

/// <summary>
/// Per-row result of <c>substrate.classify(seed_hash, junction_kind, k)</c>.
/// One row per candidate label, ordered by descending mu. Column shape:
/// (label_id, label_code, mu, sigma, games, elapsed_ms).
/// </summary>
public sealed record ClassifyResult(
    int LabelId,
    string LabelCode,
    double Mu,
    double Sigma,
    int Games,
    int ElapsedMs) : IRecordMappable<ClassifyResult>
{
    public static ClassifyResult MapFrom(NpgsqlDataReader reader) =>
        new(
            LabelId:   reader.GetInt32(0),
            LabelCode: reader.GetString(1).Trim(),
            Mu:        reader.GetDouble(2),
            Sigma:     reader.GetDouble(3),
            Games:     reader.GetInt32(4),
            ElapsedMs: reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
}
