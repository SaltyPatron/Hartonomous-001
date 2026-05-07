using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Bulk-writes junction rows into substrate tables via Npgsql. Junction
/// tables in the hash-as-PK substrate FK on entity_hash only (Phase C
/// unification — classification metadata lives on substrate.entity_classification).
/// </summary>
public sealed class NpgsqlJunctionWriter : IJunctionWriter
{
    internal const double AuthoritativeMu = 2000.0;
    internal const double AuthoritativeSigma = 50.0;
    private const int ChunkSize = 50_000;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlJunctionWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        double mu, double sigma, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        AssertSafeIdentifier(tableName);
        AssertSafeIdentifier(refColumn);
        string sql =
            $"INSERT INTO {tableName} (entity_hash, {refColumn}, mu, sigma) " +
            $"SELECT * FROM unnest($1::bytea[], $2::int[], $3::float8[], $4::float8[]) " +
            $"ON CONFLICT (entity_hash, {refColumn}) DO NOTHING";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];
            for (int i = 0; i < count; i++)
            {
                hashes[i] = entries[offset + i].EntityHash;
                refIds[i] = entries[offset + i].RefId;
                mus[i] = mu;
                sigmas[i] = sigma;
            }

            await using NpgsqlCommand cmd = new(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(refIds);
            cmd.Parameters.AddWithValue(mus);
            cmd.Parameters.AddWithValue(sigmas);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId, double Mu)> entries,
        CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        AssertSafeIdentifier(tableName);
        AssertSafeIdentifier(refColumn);
        string sql =
            $"INSERT INTO {tableName} (entity_hash, {refColumn}, mu, sigma) " +
            $"SELECT * FROM unnest($1::bytea[], $2::int[], $3::float8[], $4::float8[]) " +
            $"ON CONFLICT (entity_hash, {refColumn}) DO NOTHING";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];
            for (int i = 0; i < count; i++)
            {
                hashes[i] = entries[offset + i].EntityHash;
                refIds[i] = entries[offset + i].RefId;
                mus[i] = entries[offset + i].Mu;
                sigmas[i] = AuthoritativeSigma;
            }

            await using NpgsqlCommand cmd = new(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(refIds);
            cmd.Parameters.AddWithValue(mus);
            cmd.Parameters.AddWithValue(sigmas);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task WritePlainJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        AssertSafeIdentifier(tableName);
        AssertSafeIdentifier(refColumn);
        string sql =
            $"INSERT INTO {tableName} (entity_hash, {refColumn}) " +
            $"SELECT * FROM unnest($1::bytea[], $2::int[]) " +
            $"ON CONFLICT (entity_hash, {refColumn}) DO NOTHING";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            for (int i = 0; i < count; i++)
            {
                hashes[i] = entries[offset + i].EntityHash;
                refIds[i] = entries[offset + i].RefId;
            }

            await using NpgsqlCommand cmd = new(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            cmd.Parameters.AddWithValue(refIds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void AssertSafeIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        }
        foreach (char c in identifier)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.'))
            {
                throw new ArgumentException(
                    $"Unsafe SQL identifier: '{identifier}'", nameof(identifier));
            }
        }
    }
}
