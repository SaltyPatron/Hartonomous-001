using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Iso639;

internal sealed class Iso639ReferenceTableWriter : BaseReferenceTableWriter
{
    public Iso639ReferenceTableWriter(string connectionString)
        : base(connectionString)
    {
    }

    public async Task PopulateLanguagesAsync(
        IReadOnlyList<Iso639Record> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        string[] codes = new string[records.Count];
        string[] names = new string[records.Count];
        string[] scopes = new string[records.Count];
        string[] types = new string[records.Count];
        string?[] part1s = new string?[records.Count];
        string?[] part2bs = new string?[records.Count];
        string?[] part2ts = new string?[records.Count];

        for (int i = 0; i < records.Count; i++)
        {
            codes[i] = records[i].Id;
            names[i] = records[i].RefName;
            scopes[i] = records[i].Scope.ToString();
            types[i] = records[i].LanguageType.ToString();
            part1s[i] = records[i].Part1;
            part2bs[i] = records[i].Part2b;
            part2ts[i] = records[i].Part2t;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.language (code, name, scope, type, part1, part2b, part2t) " +
            "SELECT * FROM unnest($1::char(3)[], $2::varchar[], $3::char(1)[], $4::char(1)[], " +
            "$5::char(2)[], $6::char(3)[], $7::char(3)[]) " +
            "ON CONFLICT (code) DO UPDATE SET " +
            "part1 = EXCLUDED.part1, part2b = EXCLUDED.part2b, part2t = EXCLUDED.part2t", conn);
        cmd.Parameters.AddWithValue(codes);
        cmd.Parameters.AddWithValue(names);
        cmd.Parameters.AddWithValue(scopes);
        cmd.Parameters.AddWithValue(types);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Char, part1s);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Char, part2bs);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Char, part2ts);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task WriteLanguageJunctionsAsync(
        IReadOnlyList<(string Code, long EntityId)> nameEntities,
        Dictionary<string, int> langIdMap,
        CancellationToken ct)
    {
        List<(long EntityId, int LangId)> entries = new(nameEntities.Count);
        foreach ((string code, long entityId) in nameEntities)
        {
            if (langIdMap.TryGetValue(code, out int langId))
            {
                entries.Add((entityId, langId));
            }
        }

        await WriteEntityLanguageJunctionsAsync(entries, ct);
    }

    public async Task UpdateNameEntityIdsAsync(
        IReadOnlyList<(string Code, long EntityId)> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync(ct);

        string[] codes = new string[updates.Count];
        long[] entityIds = new long[updates.Count];
        for (int i = 0; i < updates.Count; i++)
        {
            codes[i] = updates[i].Code;
            entityIds[i] = updates[i].EntityId;
        }

        await using NpgsqlCommand cmd = new(
            "UPDATE substrate.language SET name_entity_id = t.eid " +
            "FROM unnest($1::char(3)[], $2::bigint[]) AS t(c, eid) " +
            "WHERE language.code = t.c", conn);
        cmd.Parameters.AddWithValue(codes);
        cmd.Parameters.AddWithValue(entityIds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
