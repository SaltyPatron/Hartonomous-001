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
        await using NpgsqlCommand cmd = new(
            "SELECT id, code FROM substrate.reference_code_map($1)", conn);
        cmd.Parameters.AddWithValue(tableName);
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
        await using NpgsqlCommand cmd = new(
            "SELECT id, key_text, value_text FROM substrate.reference_key_value_map($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(keyColumn);
        cmd.Parameters.AddWithValue(valueColumn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT code, value_text FROM substrate.reference_code_text_map($1, $2)", conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(valueColumn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT value FROM substrate.reference_int64_set($1, $2)", conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(columnName);
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
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.reference_id_by_code($1, $2)", conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(code);

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
        await using NpgsqlCommand cmd = new(
            "SELECT code, value_float FROM substrate.reference_code_double_map($1, $2)", conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(valueColumn);
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
        await using NpgsqlCommand cmd = new(
            "SELECT offset_doc_hash, synset_hash FROM substrate.load_wordnet_offset_synset_map()",
            conn);
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
