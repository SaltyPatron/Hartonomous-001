using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Decomposers.WordNet;

internal sealed class WordNetReferenceTableWriter : BaseReferenceTableWriter
{
    public WordNetReferenceTableWriter(string connectionString)
        : base(connectionString)
    {
    }

    public async Task<Dictionary<string, int>> LoadLexnameMapAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> map = new(50, StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new("SELECT id, code FROM substrate.lexname", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1).Trim()] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task<Dictionary<string, int>> LoadPosMapAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> map = new(20, StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new("SELECT id, code FROM substrate.pos", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1).Trim()] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task<int> LoadEnglishLanguageIdAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT id FROM substrate.language WHERE code = 'eng'", conn);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is not null
            ? Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Language 'eng' not found. ISO 639 phase must run first.");
    }

    public async Task PopulateSensesAsync(
        IReadOnlyList<(string Code, string Gloss, int LexnameId, int PosId)> senses,
        CancellationToken ct)
    {
        if (senses.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        for (int offset = 0; offset < senses.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, senses.Count - offset);
            string[] codes = new string[count];
            string[] glosses = new string[count];
            int[] lexnameIds = new int[count];
            int[] posIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                (codes[i], glosses[i], lexnameIds[i], posIds[i]) = senses[offset + i];
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.sense (code, gloss, lexname_id, pos_id) " +
                "SELECT * FROM unnest($1::varchar[], $2::text[], $3::int[], $4::int[]) " +
                "ON CONFLICT (code) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(codes);
            cmd.Parameters.AddWithValue(glosses);
            cmd.Parameters.AddWithValue(lexnameIds);
            cmd.Parameters.AddWithValue(posIds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<Dictionary<string, int>> LoadSenseMapAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> map = new(120_000, StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new("SELECT id, code FROM substrate.sense", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1).Trim()] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task WriteEntityPosJunctionsAsync(
        IReadOnlyList<(long EntityId, int PosId)> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            long[] entityIds = new long[count];
            int[] posIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];

            for (int i = 0; i < count; i++)
            {
                entityIds[i] = entries[offset + i].EntityId;
                posIds[i] = entries[offset + i].PosId;
                mus[i] = AuthoritativeMu;
                sigmas[i] = AuthoritativeSigma;
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.entity_pos (entity_id, pos_id, mu, sigma) " +
                "SELECT * FROM unnest($1::bigint[], $2::int[], $3::float8[], $4::float8[]) " +
                "ON CONFLICT (entity_id, pos_id) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(posIds);
            cmd.Parameters.AddWithValue(mus);
            cmd.Parameters.AddWithValue(sigmas);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task WriteEntitySenseJunctionsAsync(
        IReadOnlyList<(long EntityId, int SenseId, double Mu)> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            long[] entityIds = new long[count];
            int[] senseIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];

            for (int i = 0; i < count; i++)
            {
                entityIds[i] = entries[offset + i].EntityId;
                senseIds[i] = entries[offset + i].SenseId;
                mus[i] = entries[offset + i].Mu;
                sigmas[i] = AuthoritativeSigma;
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.entity_sense (entity_id, sense_id, mu, sigma) " +
                "SELECT * FROM unnest($1::bigint[], $2::int[], $3::float8[], $4::float8[]) " +
                "ON CONFLICT (entity_id, sense_id) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(senseIds);
            cmd.Parameters.AddWithValue(mus);
            cmd.Parameters.AddWithValue(sigmas);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
