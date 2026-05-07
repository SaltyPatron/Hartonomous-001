using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Npgsql;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Hash-as-PK implementation of <see cref="IPhysicalityReader"/>. Reads the
/// substrate.physicality.geom column (PostGIS POINTZM / LINESTRINGZM) and
/// decodes the four spatial coordinates per point. The substrate's 4D
/// physicality lives entirely on PostGIS-native geometry; the M coordinate
/// is a real spatial axis treated as such by the substrate.st_4d_*
/// function family.
///
/// Addresses entities by composite (entity_type_id, entity_hash) handle —
/// no surrogate id columns referenced.
/// </summary>
public sealed class NpgsqlPhysicalityReader : IPhysicalityReader
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlPhysicalityReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<double[]?> GetLineString4dAsync(
        EntityHandle entity, string physicalityTypeCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
                await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                        conn,
                        SubstrateFunctionNames.PhysicalityLineString4d,
                        entity.Hash,
                        entity.EntityTypeCode,
                        physicalityTypeCode);
        object? raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is not double[] coords || coords.Length < 4 || (coords.Length % 4) != 0)
        {
            return null;
        }
        return coords;
    }

    public async Task<(double X1, double X2, double X3, double X4)?> GetPoint4dAsync(
        EntityHandle entity, string physicalityTypeCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
                await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                        conn,
                        SubstrateFunctionNames.PhysicalityPoint4d,
                        entity.Hash,
                        entity.EntityTypeCode,
                        physicalityTypeCode);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
    }
}
