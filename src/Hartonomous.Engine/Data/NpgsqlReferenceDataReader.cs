using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Reads code→id mappings from substrate reference tables via Npgsql.
/// Consolidates the identical logic from <c>CodeResolver</c> and
/// <c>BaseReferenceTableWriter.LoadCodeMapAsync</c>.
/// </summary>
public sealed class NpgsqlReferenceDataReader : IReferenceDataReader
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlReferenceDataReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Dictionary<string, int>> LoadCodeMapAsync(
        string tableName, int initialCapacity, CancellationToken ct)
    {
        Dictionary<string, int> map = new(initialCapacity, StringComparer.Ordinal);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceCodeMap,
            new object?[] { tableName });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1).Trim()] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task<Dictionary<(string Key, string Value), int>> LoadKeyValueMapAsync(
        string tableName, string keyColumn, string valueColumn,
        int initialCapacity, CancellationToken ct)
    {
        Dictionary<(string, string), int> map = new(initialCapacity);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceKeyValueMap,
            new object?[] { tableName, keyColumn, valueColumn });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[(reader.GetString(1).Trim(), reader.GetString(2).Trim())] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task<Dictionary<string, string>> LoadCodeTextMapAsync(
        string tableName, string valueColumn, int initialCapacity, CancellationToken ct)
    {
        Dictionary<string, string> map = new(initialCapacity, StringComparer.Ordinal);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceCodeTextMap,
            new object?[] { tableName, valueColumn });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0).Trim()] = reader.GetString(1);
        }
        return map;
    }

    public async Task<HashSet<long>> LoadInt64SetAsync(
        string tableName, string columnName, CancellationToken ct)
    {
        HashSet<long> values = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceInt64Set,
            new object?[] { tableName, columnName });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetInt64(0));
        }
        return values;
    }

    public async Task<int> LoadIdByCodeAsync(
        string tableName, string code, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceIdByCode,
            new object?[] { tableName, code });

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is not null
            ? Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException(
                $"Code '{code}' not found in reference table '{tableName}'.");
    }

    public async Task<Dictionary<string, double>> LoadCodeDoubleMapAsync(
        string tableName, string valueColumn, int initialCapacity, CancellationToken ct)
    {
        Dictionary<string, double> map = new(initialCapacity, StringComparer.Ordinal);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ReferenceCodeDoubleMap,
            new object?[] { tableName, valueColumn });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0).Trim()] = reader.GetDouble(1);
        }
        return map;
    }

    public async Task<Dictionary<string, int>> LoadLanguageAliasMapAsync(CancellationToken ct)
    {
        // Substrate.language has 4 ISO-form columns: code (639-3), part1 (639-1),
        // part2b (639-2/B), part2t (639-2/T). Read them all in one pass; build
        // alias → canonical-id map. ~8k rows × ~2 populated forms each ≈ 16k entries.
        Dictionary<string, int> map = new(16384, StringComparer.OrdinalIgnoreCase);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        const string sql = "SELECT id, code, part1, part2b, part2t FROM substrate.language";
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.CommandTimeout = 60;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int id = reader.GetInt32(0);
            AddNonNullAlias(map, reader, 1, id);  // code (NOT NULL)
            AddNonNullAlias(map, reader, 2, id);  // part1 (nullable)
            AddNonNullAlias(map, reader, 3, id);  // part2b (nullable)
            AddNonNullAlias(map, reader, 4, id);  // part2t (nullable)
        }
        return map;
    }

    private static void AddNonNullAlias(Dictionary<string, int> map, NpgsqlDataReader reader, int ordinal, int id)
    {
        if (reader.IsDBNull(ordinal)) { return; }
        string alias = reader.GetString(ordinal).Trim();
        if (alias.Length == 0) { return; }
        // First-write wins. The substrate.language seed is well-formed so any
        // collision would indicate seed-data inconsistency worth investigating.
        map.TryAdd(alias, id);
    }
}
