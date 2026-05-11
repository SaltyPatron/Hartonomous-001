using System.Globalization;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

internal static class UnicodeSql
{
    public const int MaxCodepoints = 0x110000;
    public const int PropertyChunkSize = 32768;
    public const int AtomParallelism = 8;

    public static async Task<string> ExecuteScalarStringAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static async Task<long> ExecuteScalarLongAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(sql, connection);
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
            await using NpgsqlCommand command = new(
                "SELECT substrate.populate_codepoint_property_range_from_ext($1::int, $2::int)",
                connection);
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue(lo);
            command.Parameters.AddWithValue(count);
            object? value = await command.ExecuteScalarAsync(ct);
            total += Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return total;
    }

    public static async Task<long> PopulateUnicodeCaseEdgesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using NpgsqlCommand command = new(
            "SELECT substrate.populate_unicode_case_edges_from_properties()",
            connection);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> PopulateCodepointAtomRangeAsync(
        NpgsqlDataSource dataSource,
        int lo,
        int hi,
        CancellationToken ct)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand command = new(
            "SELECT substrate.populate_codepoint_atoms_chunk($1::text, NULL::float8, $2::int, $3::int)",
            connection);
        command.CommandTimeout = 0;
        command.Parameters.AddWithValue("unicode_consortium");
        command.Parameters.AddWithValue(lo);
        command.Parameters.AddWithValue(hi);
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
