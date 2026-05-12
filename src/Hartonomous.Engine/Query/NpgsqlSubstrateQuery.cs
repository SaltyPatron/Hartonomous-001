using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Query;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Query;

/// <summary>
/// Hash-only implementation of <see cref="ISubstrateQuery"/>. Per Phase C
/// unification, <c>substrate.entity</c> has hash-only PK; classification
/// (entity_type) is metadata on <c>substrate.entity_classification</c>.
/// <c>substrate.edge_member</c>, <c>entity_significance</c>,
/// <c>entity_model_source</c>, and <c>tensor_tensor_role</c> all reference
/// entities by hash only. Edge identity stays composite
/// <c>(edge_type_id, edge_hash)</c> because edge type IS structural.
///
/// Type filtering and type projection both flow through
/// <c>substrate.entity_classification</c> joined to
/// <c>substrate.entity_type</c>. The advanced-pass entity types
/// (embedding_position, ffn_neuron, attention_component, etc.) and edge
/// types (has_embedding_position, has_ffn_neuron, etc.) are added at
/// decomposer runtime; when the substrate has not been ingested with
/// those passes, the corresponding methods return empty lists.
/// </summary>
public sealed class NpgsqlSubstrateQuery : ISubstrateQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlSubstrateQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryEntitiesAsync(
        SubstrateQueryFilter filter, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryEntities,
            [
                TextArrayParameter(filter.EntityTypeCodes),
                IntArrayParameter(filter.ModelSourceIds),
                DoubleParameter(filter.MinSignificanceMu),
                TextParameter(string.IsNullOrEmpty(filter.ContextTypeCode) ? null : filter.ContextTypeCode),
                IntParameter(filter.Limit)
            ]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryTensorsForArchitectureAsync(
        EntityHandle modelArchitecture, SubstrateQueryFilter filter, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryTensorsForArchitecture,
            [
                TextParameter(modelArchitecture.EntityTypeCode),
                ByteaParameter(modelArchitecture.Hash),
                IntArrayParameter(filter.ModelSourceIds),
                DoubleParameter(filter.MinSignificanceMu),
                TextParameter(string.IsNullOrEmpty(filter.ContextTypeCode) ? null : filter.ContextTypeCode),
                IntParameter(filter.Limit)
            ]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<PackageTensorHandle>> QueryTensorsForModelSourceAsync(
        long modelSourceId,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryTensorsForModelSource,
            [IntParameter(checked((int)modelSourceId))]);

        List<PackageTensorHandle> results = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string packageTypeCode = reader.GetString(0).Trim();
            byte[] packageHash = (byte[])reader.GetValue(1);
            int ordinal = reader.GetInt32(2);
            string occurrenceTypeCode = reader.GetString(3).Trim();
            byte[] occurrenceHash = (byte[])reader.GetValue(4);
            string tensorTypeCode = reader.GetString(5).Trim();
            byte[] tensorHash = (byte[])reader.GetValue(6);
            results.Add(new PackageTensorHandle(
                new EntityHandle(packageHash, packageTypeCode),
                ordinal,
                new EntityHandle(occurrenceHash, occurrenceTypeCode),
                new EntityHandle(tensorHash, tensorTypeCode)));
        }
        return results;
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryFireflyForVocabAsync(
        IReadOnlyList<EntityHandle> bpeTokens,
        double minSignificanceMu,
        string contextTypeCode,
        int? limit,
        CancellationToken ct)
    {
        if (bpeTokens.Count == 0)
        {
            return [];
        }

        // Pull supplied vocabulary entity hashes; the query returns those
        // word_form handles only when they carry embedding_firefly physicality.
        byte[][] bpeHashes = new byte[bpeTokens.Count][];
        for (int i = 0; i < bpeTokens.Count; i++)
        {
            bpeHashes[i] = bpeTokens[i].Hash.ToByteArray();
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryFirefliesForVocab,
            [
                ByteaArrayParameter(bpeHashes),
                DoubleParameter(minSignificanceMu),
                TextParameter(contextTypeCode),
                IntParameter(limit)
            ]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryFfnNeuronsByHiddenDimAsync(
        int hiddenSize,
        int topK,
        string contextTypeCode,
        CancellationToken ct)
    {
        // ffn_neuron parents live as the source of has_ffn_neuron edges; the
        // tensor's hidden dim is encoded on a has_hidden_size edge to a text
        // composition carrying the numeric literal. Match by recomposed string
        // value — at decomposer time the literal is hashed via Merkle of
        // codepoints, so we can compute the same hash here and match by hash.
        byte[] hiddenSizeHash = Hartonomous.Core.Compute.Common.Blake3.Hash(
            Encoding.UTF8.GetBytes(hiddenSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryFfnNeuronsByHiddenDim,
            [hiddenSizeHash, contextTypeCode, topK]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryAttentionComponentsAsync(
        int headDim,
        EntityHandle? archetype,
        int topK,
        string contextTypeCode,
        CancellationToken ct)
    {
        _ = headDim; // informational; downstream verifies shape compatibility per scatter call.
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QueryAttentionComponents,
            [
                ByteaParameter(archetype?.Hash),
                TextParameter(contextTypeCode),
                IntParameter(topK)
            ]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.QuerySingularDirectionsForRole,
            [tensorRoleCode, topK]);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    private static async Task<IReadOnlyList<EntityHandle>> ReadHandlesAsync(
        NpgsqlCommand cmd, bool useTypeId, CancellationToken ct)
    {
        _ = useTypeId; // legacy parameter retained for caller signature stability; ignored.
        List<EntityHandle> results = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            results.Add(new EntityHandle(hash, typeCode));
        }
        return results;
    }

    private static NpgsqlParameter TextArrayParameter(IReadOnlyList<string>? values)
        => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = values is { Count: > 0 } ? values : DBNull.Value };

    private static NpgsqlParameter ByteaArrayParameter(byte[][] values)
        => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = values };

    private static NpgsqlParameter IntArrayParameter(IReadOnlyList<long>? values)
        => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer, Value = ToIntArray(values) ?? (object)DBNull.Value };

    private static NpgsqlParameter ByteaParameter(byte[]? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = value ?? (object)DBNull.Value };

    private static NpgsqlParameter ByteaParameter(Hash32 value)
        => ByteaParameter(value.ToByteArray());

    private static NpgsqlParameter ByteaParameter(Hash32? value)
        => value.HasValue ? ByteaParameter(value.Value) : ByteaParameter((byte[]?)null);

    private static NpgsqlParameter TextParameter(string? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value ?? (object)DBNull.Value };

    private static NpgsqlParameter DoubleParameter(double? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Double, Value = value ?? (object)DBNull.Value };

    private static NpgsqlParameter IntParameter(int? value)
        => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = value ?? (object)DBNull.Value };

    private static int[]? ToIntArray(IReadOnlyList<long>? values)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        int[] converted = new int[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            converted[index] = checked((int)values[index]);
        }

        return converted;
    }

    // ── CLI-surface helpers ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<(string Code, long Value, string? Detail)>> GetModelInventoryAsync(
        byte[] archHash, CancellationToken ct)
    {
        List<(string, long, string?)> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ModelInventory,
            [archHash]);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add((r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        results.Sort(static (left, right) => string.CompareOrdinal(left.Item1, right.Item1));
        return results;
    }

    public async Task<long> GetModelVocabRecoveredAsync(byte[] archHash, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ModelVocabRecovered,
            [archHash]);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(string EdgeType, double SrcOnly, double Consensus, double Delta, bool Above)>> GetRefinementSummaryAsync(
        byte[] archHash, string arenaCode, int limit, CancellationToken ct)
    {
        List<(string, double, double, double, bool)> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.RefinementSummaryTop,
            [archHash, arenaCode, limit]);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetDouble(1), r.IsDBNull(2) ? 0 : r.GetDouble(2),
                r.IsDBNull(3) ? 0 : r.GetDouble(3), !r.IsDBNull(4) && r.GetBoolean(4)));
        }
        return results;
    }

    public async Task<IReadOnlyList<(int Idx, string TensorHashHex, double Claimed, double Actual, bool Verified, string Detail)>> AuditWalkAsync(
        string chainJson, CancellationToken ct)
    {
        List<(int, string, double, double, bool, string)> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.RecomposeAuditWalk,
            [new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = chainJson }]);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            byte[] tensorHash = (byte[])r.GetValue(1);
            results.Add((r.GetInt32(0), Convert.ToHexString(tensorHash), r.IsDBNull(2) ? 0 : r.GetDouble(2),
                r.IsDBNull(3) ? double.NaN : r.GetDouble(3), r.GetBoolean(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        }
        return results;
    }

    public async Task<(string? Answer, byte[]? TargetHash, double Confidence, int SeedCount, long TargetCount, int ElapsedMs)?> SubstrateRecallAsync(
        byte[] promptHash, int maxSeeds, int maxTargets, double minConfidence, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.Recall,
            [promptHash, maxSeeds, maxTargets, minConfidence]);
        cmd.CommandTimeout = 300;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : (byte[])r.GetValue(1),
            r.IsDBNull(2) ? 0.0 : r.GetDouble(2),
            r.IsDBNull(3) ? 0 : r.GetInt32(3),
            r.IsDBNull(4) ? 0 : r.GetInt64(4),
            r.IsDBNull(5) ? 0 : r.GetInt32(5));
    }
}
