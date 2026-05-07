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

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];
            for (int entryIndex = 0; entryIndex < count; entryIndex++)
            {
                hashes[entryIndex] = entries[offset + entryIndex].EntityHash;
                refIds[entryIndex] = entries[offset + entryIndex].RefId;
                mus[entryIndex] = mu;
                sigmas[entryIndex] = sigma;
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateProcedure(
                conn,
                SubstrateProcedureNames.WriteGlickoJunction,
                [
                    CreateParameter(NpgsqlDbType.Text, tableName),
                    CreateParameter(NpgsqlDbType.Text, refColumn),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Bytea, hashes),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Integer, refIds),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Double, mus),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Double, sigmas),
                ]);
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

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            double[] mus = new double[count];
            double[] sigmas = new double[count];
            for (int entryIndex = 0; entryIndex < count; entryIndex++)
            {
                hashes[entryIndex] = entries[offset + entryIndex].EntityHash;
                refIds[entryIndex] = entries[offset + entryIndex].RefId;
                mus[entryIndex] = entries[offset + entryIndex].Mu;
                sigmas[entryIndex] = AuthoritativeSigma;
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateProcedure(
                conn,
                SubstrateProcedureNames.WriteGlickoJunction,
                [
                    CreateParameter(NpgsqlDbType.Text, tableName),
                    CreateParameter(NpgsqlDbType.Text, refColumn),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Bytea, hashes),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Integer, refIds),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Double, mus),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Double, sigmas),
                ]);
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

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        for (int offset = 0; offset < entries.Count; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, entries.Count - offset);
            byte[][] hashes = new byte[count][];
            int[] refIds = new int[count];
            for (int entryIndex = 0; entryIndex < count; entryIndex++)
            {
                hashes[entryIndex] = entries[offset + entryIndex].EntityHash;
                refIds[entryIndex] = entries[offset + entryIndex].RefId;
            }

            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateProcedure(
                conn,
                SubstrateProcedureNames.WritePlainJunction,
                [
                    CreateParameter(NpgsqlDbType.Text, tableName),
                    CreateParameter(NpgsqlDbType.Text, refColumn),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Bytea, hashes),
                    CreateParameter(NpgsqlDbType.Array | NpgsqlDbType.Integer, refIds),
                ]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static NpgsqlParameter CreateParameter(NpgsqlDbType type, object value)
        => new() { NpgsqlDbType = type, Value = value };
}
