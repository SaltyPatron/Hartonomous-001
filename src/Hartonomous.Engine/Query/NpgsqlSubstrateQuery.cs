using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Query;
using Npgsql;

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
/// (embedding_firefly, ffn_neuron, attention_component, etc.) and edge
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
        // Type code is now metadata on entity_classification (Phase C). Always
        // join through it so callers receive (type_code, hash) handles. Multiple
        // classifications per content => multiple handles for the same hash.
        StringBuilder sql = new();
        sql.Append("SELECT DISTINCT et.code, e.hash FROM substrate.entity e ");
        sql.Append("JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash ");
        sql.Append("JOIN substrate.entity_type et ON et.id = ec.entity_type_id ");

        List<string> wheres = [];
        List<NpgsqlParameter> parameters = [];

        if (filter.EntityTypeCodes is { Count: > 0 })
        {
            wheres.Add("et.code = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.EntityTypeCodes });
        }
        if (filter.ModelSourceIds is { Count: > 0 })
        {
            sql.Append("JOIN substrate.entity_model_source ems ON ems.entity_hash = e.hash ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.entity_significance s ON s.entity_hash = e.hash ");
            wheres.Add("s.mu >= $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = minMu });
            if (!string.IsNullOrEmpty(filter.ContextTypeCode))
            {
                sql.Append("JOIN substrate.significance_context sc ON sc.id = s.context_type_id ");
                wheres.Add("sc.code = $" + (parameters.Count + 1));
                parameters.Add(new NpgsqlParameter { Value = filter.ContextTypeCode });
            }
        }

        if (wheres.Count > 0)
        {
            sql.Append("WHERE ");
            sql.Append(string.Join(" AND ", wheres));
            sql.Append(' ');
        }

        if (filter.MinSignificanceMu.HasValue)
        {
            sql.Append("ORDER BY s.mu DESC, e.hash ASC ");
        }
        else
        {
            sql.Append("ORDER BY et.code, e.hash ASC ");
        }

        if (filter.Limit is int lim)
        {
            sql.Append("LIMIT ").Append(lim);
        }

        return await ReadHandlesAsync(sql.ToString(), parameters, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryTensorsForArchitectureAsync(
        EntityHandle modelArchitecture, SubstrateQueryFilter filter, CancellationToken ct)
    {
        // edge_member is hash-only. Type filtering on src and projection of
        // tgt's type code go through entity_classification.
        StringBuilder sql = new();
        sql.Append(@"
            SELECT DISTINCT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id AND et.code = 'has_tensor'
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_classification ec_s ON ec_s.entity_hash = em_s.entity_hash
              JOIN substrate.entity_type src_et ON src_et.id = ec_s.entity_type_id
              JOIN substrate.entity_classification ec_t ON ec_t.entity_hash = em_t.entity_hash
              JOIN substrate.entity_type tgt_et ON tgt_et.id = ec_t.entity_type_id
        ");

        List<string> wheres =
        [
            "src_et.code = $1",
            "em_s.entity_hash = $2",
        ];
        List<NpgsqlParameter> parameters =
        [
            new() { Value = modelArchitecture.EntityTypeCode },
            new() { Value = modelArchitecture.Hash },
        ];

        if (filter.ModelSourceIds is { Count: > 0 })
        {
            sql.Append("JOIN substrate.entity_model_source ems ON ems.entity_hash = em_t.entity_hash ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.entity_significance s ON s.entity_hash = em_t.entity_hash ");
            wheres.Add("s.mu >= $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = minMu });
            if (!string.IsNullOrEmpty(filter.ContextTypeCode))
            {
                sql.Append("JOIN substrate.significance_context sc ON sc.id = s.context_type_id ");
                wheres.Add("sc.code = $" + (parameters.Count + 1));
                parameters.Add(new NpgsqlParameter { Value = filter.ContextTypeCode });
            }
        }

        sql.Append(" WHERE ").Append(string.Join(" AND ", wheres));
        sql.Append(filter.MinSignificanceMu.HasValue
            ? " ORDER BY s.mu DESC, em_t.entity_hash ASC"
            : " ORDER BY em_t.entity_hash ASC");
        if (filter.Limit is int lim)
        {
            sql.Append(" LIMIT ").Append(lim);
        }

        return await ReadHandlesAsync(sql.ToString(), parameters, ct);
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

        // Pull bpe_token entity hashes; the query restricts to bpe_token type
        // on the source side via entity_type_code = 'bpe_token'.
        byte[][] bpeHashes = new byte[bpeTokens.Count][];
        for (int i = 0; i < bpeTokens.Count; i++)
        {
            bpeHashes[i] = bpeTokens[i].Hash;
        }

        StringBuilder sql = new();
        sql.Append(@"
            SELECT DISTINCT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_classification ec_s ON ec_s.entity_hash = em_s.entity_hash
              JOIN substrate.entity_type src_et ON src_et.id = ec_s.entity_type_id
              JOIN substrate.entity_classification ec_t ON ec_t.entity_hash = em_t.entity_hash
              JOIN substrate.entity_type tgt_et ON tgt_et.id = ec_t.entity_type_id
              JOIN substrate.entity_significance s ON s.entity_hash = em_t.entity_hash
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
             WHERE et.code = 'has_embedding_position'
               AND tgt_et.code = 'embedding_firefly'
               AND src_et.code = 'word_form'
               AND em_s.entity_hash = ANY($1)
               AND s.mu >= $2
               AND sc.code = $3
             ORDER BY s.mu DESC, em_t.entity_hash ASC
        ");
        if (limit is int lim)
        {
            sql.Append(" LIMIT ").Append(lim);
        }

        List<NpgsqlParameter> parameters =
        [
            new() { Value = bpeHashes },
            new() { Value = minSignificanceMu },
            new() { Value = contextTypeCode },
        ];
        return await ReadHandlesAsync(sql.ToString(), parameters, ct);
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

        const string sql = @"
            SELECT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_classification ec_t ON ec_t.entity_hash = em_t.entity_hash
              JOIN substrate.entity_type tgt_et ON tgt_et.id = ec_t.entity_type_id
              JOIN substrate.entity_significance s ON s.entity_hash = em_t.entity_hash
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
              JOIN substrate.edge size_e ON size_e.edge_type_id =
                   (SELECT id FROM substrate.edge_type WHERE code = 'has_hidden_size')
              JOIN substrate.edge_member size_s ON size_s.edge_type_id = size_e.edge_type_id AND size_s.edge_hash = size_e.hash
              JOIN substrate.edge_role size_sr ON size_sr.id = size_s.edge_role_id AND size_sr.code = 'source'
              JOIN substrate.edge_member size_t ON size_t.edge_type_id = size_e.edge_type_id AND size_t.edge_hash = size_e.hash
              JOIN substrate.edge_role size_tr ON size_tr.id = size_t.edge_role_id AND size_tr.code = 'target'
             WHERE et.code = 'has_ffn_neuron'
               AND tgt_et.code = 'ffn_neuron'
               AND sc.code = $1
               AND size_s.entity_hash = em_s.entity_hash
               AND size_t.entity_hash = $2
             ORDER BY s.mu DESC
             LIMIT $3";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue(contextTypeCode);
        cmd.Parameters.AddWithValue(hiddenSizeHash);
        cmd.Parameters.AddWithValue(topK);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> QueryAttentionComponentsAsync(
        int headDim,
        EntityHandle? archetype,
        int topK,
        string contextTypeCode,
        CancellationToken ct)
    {
        StringBuilder sql = new();
        sql.Append(@"
            SELECT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_classification ec_t ON ec_t.entity_hash = em_t.entity_hash
              JOIN substrate.entity_type tgt_et ON tgt_et.id = ec_t.entity_type_id
              JOIN substrate.entity_significance s ON s.entity_hash = em_t.entity_hash
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
        ");

        List<string> wheres =
        [
            "et.code = 'has_attention_component'",
            "tgt_et.code = 'attention_component'",
            "sc.code = $1",
        ];
        List<NpgsqlParameter> parameters = [new() { Value = contextTypeCode }];

        if (archetype is EntityHandle aHandle)
        {
            sql.Append(@"
              JOIN substrate.edge arch_e ON arch_e.edge_type_id =
                   (SELECT id FROM substrate.edge_type WHERE code = 'encodes_archetype')
              JOIN substrate.edge_member arch_s ON arch_s.edge_type_id = arch_e.edge_type_id AND arch_s.edge_hash = arch_e.hash
              JOIN substrate.edge_role arch_sr ON arch_sr.id = arch_s.edge_role_id AND arch_sr.code = 'source'
              JOIN substrate.edge_member arch_t ON arch_t.edge_type_id = arch_e.edge_type_id AND arch_t.edge_hash = arch_e.hash
              JOIN substrate.edge_role arch_tr ON arch_tr.id = arch_t.edge_role_id AND arch_tr.code = 'target'
            ");
            wheres.Add("arch_s.entity_hash = em_s.entity_hash");
            wheres.Add("arch_t.entity_hash = $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = aHandle.Hash });
        }
        _ = headDim; // informational; downstream verifies shape compatibility per scatter call.
        sql.Append(" WHERE ").Append(string.Join(" AND ", wheres));
        sql.Append(" ORDER BY s.mu DESC LIMIT $").Append(parameters.Count + 1);
        parameters.Add(new NpgsqlParameter { Value = topK });

        return await ReadHandlesAsync(sql.ToString(), parameters, ct, useTypeId: false);
    }

    public async Task<IReadOnlyList<EntityHandle>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_classification ec_t ON ec_t.entity_hash = em_t.entity_hash
              JOIN substrate.entity_type tgt_et ON tgt_et.id = ec_t.entity_type_id
              JOIN substrate.tensor_tensor_role ttr ON ttr.entity_hash = em_s.entity_hash
              JOIN substrate.tensor_role tr ON tr.id = ttr.tensor_role_id
             WHERE et.code = 'has_rank_component'
               AND tr.code = $1
             ORDER BY e.hash ASC
             LIMIT $2";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue(tensorRoleCode);
        cmd.Parameters.AddWithValue(topK);
        return await ReadHandlesAsync(cmd, useTypeId: false, ct);
    }

    private async Task<IReadOnlyList<EntityHandle>> ReadHandlesAsync(
        string sql, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken ct,
        bool useTypeId = false)
    {
        _ = useTypeId; // legacy parameter retained for caller signature stability; ignored.
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach (NpgsqlParameter p in parameters)
        {
            cmd.Parameters.Add(p);
        }
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

    // ── CLI-surface helpers ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<(string Code, long Value, string? Detail)>> GetModelInventoryAsync(
        byte[] archHash, CancellationToken ct)
    {
        List<(string, long, string?)> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT metric_code, metric_value, metric_detail FROM substrate.model_inventory($1) ORDER BY metric_code", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = archHash });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add((r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }
        return results;
    }

    public async Task<long> GetModelVocabRecoveredAsync(byte[] archHash, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new("SELECT substrate.model_vocab_recovered($1)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = archHash });
        object? result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(string EdgeType, double SrcOnly, double Consensus, double Delta, bool Above)>> GetRefinementSummaryAsync(
        byte[] archHash, string arenaCode, int limit, CancellationToken ct)
    {
        List<(string, double, double, double, bool)> results = [];
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_type_code, source_only_mu, consensus_mu, delta_mu, above_threshold " +
            "FROM substrate.refinement_summary($1, $2) " +
            "ORDER BY delta_mu DESC NULLS LAST LIMIT $3", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = archHash });
        cmd.Parameters.AddWithValue(arenaCode);
        cmd.Parameters.AddWithValue(limit);
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
        await using NpgsqlCommand cmd = new(
            "SELECT chain_index, encode(tensor_hash, 'hex'), claimed_mu, actual_mu, verified, detail " +
            "FROM substrate.recompose_audit_walk($1::jsonb)", conn);
        cmd.Parameters.AddWithValue(chainJson);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetDouble(2),
                r.IsDBNull(3) ? double.NaN : r.GetDouble(3), r.GetBoolean(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        }
        return results;
    }

    public async Task<(string? Answer, byte[]? TargetHash, double Confidence, int SeedCount, long TargetCount, int ElapsedMs)?> SubstrateRecallAsync(
        byte[] promptHash, int maxSeeds, int maxTargets, double minConfidence, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT answer, target_hash, confidence, seed_count, target_count, elapsed_ms " +
            "FROM substrate.recall($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(promptHash);
        cmd.Parameters.AddWithValue(maxSeeds);
        cmd.Parameters.AddWithValue(maxTargets);
        cmd.Parameters.AddWithValue(minConfidence);
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
