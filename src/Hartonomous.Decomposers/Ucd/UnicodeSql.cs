using System.Globalization;
using Hartonomous.Core.Data;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Ucd;

internal static class UnicodeSql
{
    public const int MaxCodepoints = 0x110000;
    public const int PropertyChunkSize = 32768;
    public const int AtomParallelism = 8;

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

    public static async Task<long> PopulateCodepointAtomsAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        int chunkSize = (int)Math.Ceiling((double)MaxCodepoints / AtomParallelism);
        Task<long>[] tasks = new Task<long>[AtomParallelism];
        for (int i = 0; i < AtomParallelism; i++)
        {
            int lo = i * chunkSize;
            int hi = Math.Min(lo + chunkSize, MaxCodepoints);
            tasks[i] = PopulateCodepointAtomRangeAsync(dataSource, lo, hi, ct);
        }

        long[] counts = await Task.WhenAll(tasks);
        return counts.Sum();
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

    public static async Task<long> PopulateUnicodeCaseEdgesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeCaseEdgesFromProperties);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeDecompositionEdgesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeDecompositionEdgesFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeFullCaseMappingEdgesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeFullCaseMappingEdgesFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeConfusablesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeConfusablesFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeStandardizedVariantsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeStandardizedVariantsFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeRadicalStrokeAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeRadicalStrokeFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeNamedSequencesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeNamedSequencesFromExt);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static async Task<long> PopulateUnicodeEmojiSequencesAsync(
        NpgsqlConnection connection,
        bool useZwj,
        CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateUnicodeEmojiSequencesFromExt,
            new object?[] { useZwj });
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

    private static async Task<long> PopulateCodepointAtomRangeAsync(
        NpgsqlDataSource dataSource,
        int lo,
        int hi,
        CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.PopulateCodepointAtomsChunk,
            new[]
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "unicode_consortium" },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = DBNull.Value },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = lo },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = hi },
            });
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
