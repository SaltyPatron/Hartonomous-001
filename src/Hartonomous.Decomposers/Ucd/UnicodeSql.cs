using System.Globalization;
using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Thin Npgsql helpers for substrate functions still in use by Unicode passes.
/// Substrate-ingestion populate_*_from_ext functions were retired 2026-05-17
/// per Step J of the ancient-launching-papert plan; substrate.entity/edge
/// content now comes from C# producer passes under src/Hartonomous.Decomposers/Ucd/.
///
/// What remains:
///  - PopulateCodepointPropertiesAsync: feeds the codepoint_property junction
///    table (app-data infrastructure per the three-tier distinction; NOT
///    substrate content; legitimate populate_*_from_ext consumer).
///  - LoadMaterializationCountsAsync: post-pass count verification used by
///    UnicodeMaterializationValidationPass.
/// </summary>
internal static class UnicodeSql
{
    public const int MaxCodepoints = 0x110000;
    public const int PropertyChunkSize = 32768;

    public static async Task<string> ExecuteScalarStringAsync(NpgsqlConnection connection, string functionName, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(connection, functionName);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static async Task<long> ExecuteScalarLongAsync(NpgsqlConnection connection, string functionName, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(connection, functionName);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateCodepointPropertiesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        long total = 0;
        for (int lo = 0; lo < MaxCodepoints; lo += PropertyChunkSize)
        {
            int count = Math.Min(PropertyChunkSize, MaxCodepoints - lo);
            await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
                connection,
                SubstrateFunctionNames.PopulateCodepointPropertyRangeFromExt,
                new object?[] { lo, count });
            command.CommandTimeout = 0;
            object? value = await command.ExecuteScalarAsync(ct);
            total += Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return total;
    }

    public static async Task<UcdMaterializationCounts> LoadMaterializationCountsAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.UcdMaterializationCounts);
        command.CommandTimeout = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("substrate.ucd_materialization_counts() returned no rows.");
        }

        return new UcdMaterializationCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }
}
