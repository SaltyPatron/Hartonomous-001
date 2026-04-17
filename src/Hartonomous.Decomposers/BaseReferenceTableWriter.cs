using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Decomposers;

internal abstract class BaseReferenceTableWriter
{
    protected const double AuthoritativeMu = 2000.0;
    protected const double AuthoritativeSigma = 50.0;
    protected const int ChunkSize = 50_000;

    protected readonly NpgsqlDataSource DataSource;

    protected BaseReferenceTableWriter(string connectionString)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString);
        DataSource = builder.Build();
    }

    public async Task<Dictionary<string, int>> LoadLanguageCodeMapAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> map = new(8000, StringComparer.Ordinal);
        await using NpgsqlCommand cmd = new("SELECT id, code FROM substrate.language", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1).Trim()] = reader.GetInt32(0);
        }
        return map;
    }

    public async Task WriteEntityLanguageJunctionsAsync(
        IReadOnlyList<(long EntityId, int LangId)> entries, CancellationToken ct)
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
            int[] langIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];

            for (int i = 0; i < count; i++)
            {
                entityIds[i] = entries[offset + i].EntityId;
                langIds[i] = entries[offset + i].LangId;
                mus[i] = AuthoritativeMu;
                sigmas[i] = AuthoritativeSigma;
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.entity_language (entity_id, language_id, mu, sigma) " +
                "SELECT * FROM unnest($1::bigint[], $2::int[], $3::float8[], $4::float8[]) " +
                "ON CONFLICT (entity_id, language_id) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(langIds);
            cmd.Parameters.AddWithValue(mus);
            cmd.Parameters.AddWithValue(sigmas);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task WriteEntityLanguageJunctionsAsync(
        IReadOnlyList<long> entityIds, int languageId, CancellationToken ct)
    {
        if (entityIds.Count == 0)
        {
            return;
        }

        List<(long EntityId, int LangId)> entries = new(entityIds.Count);
        foreach (long eid in entityIds)
        {
            entries.Add((eid, languageId));
        }

        await WriteEntityLanguageJunctionsAsync(entries, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
    }
}
