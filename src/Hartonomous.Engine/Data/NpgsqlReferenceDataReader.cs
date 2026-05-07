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
            tableName);
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
            tableName,
            keyColumn,
            valueColumn);
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
            tableName,
            valueColumn);
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
            tableName,
            columnName);
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
            tableName,
            code);

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
            tableName,
            valueColumn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(0).Trim()] = reader.GetDouble(1);
        }
        return map;
    }

    public async Task<Dictionary<byte[], byte[]>> LoadWordNetOffsetSynsetMapAsync(
        CancellationToken ct)
    {
        Dictionary<byte[], byte[]> map = new(120_000, ByteArrayEqualityComparer.Instance);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.LoadWordNetOffsetSynsetMap);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[] offsetDocHash = (byte[])reader[0];
            byte[] synsetHash = (byte[])reader[1];
            map[offsetDocHash] = synsetHash;
        }
        return map;
    }
}
