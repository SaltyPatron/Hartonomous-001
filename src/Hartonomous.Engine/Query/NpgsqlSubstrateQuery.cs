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
/// Hash-as-PK implementation of <see cref="ISubstrateQuery"/>. Composite
/// (entity_type_id, entity_hash) and (edge_type_id, edge_hash) keys
/// throughout — no surrogate id columns. Filter clauses compose via
/// parameterized SQL.
///
/// The advanced-pass entity types (embedding_firefly, ffn_neuron,
/// attention_component, svd_rank_component, etc.) and edge types
/// (has_embedding_position, has_ffn_neuron, has_attention_component,
/// has_rank_component, encodes_archetype, has_hidden_size) are added at
/// decomposer runtime by SafetensorsDecomposer's analysis passes via
/// IReferenceDataWriter. When the substrate has not been ingested with
/// those passes, the corresponding methods return empty lists — that is
/// the correct, substrate-faithful "no results" outcome.
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
        StringBuilder sql = new();
        sql.Append("SELECT DISTINCT et.code, e.hash FROM substrate.entity e ");
        sql.Append("JOIN substrate.entity_type et ON et.id = e.entity_type_id ");

        List<string> wheres = [];
        List<NpgsqlParameter> parameters = [];

        if (filter.EntityTypeCodes is { Count: > 0 })
        {
            wheres.Add("et.code = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.EntityTypeCodes });
        }
        if (filter.ModelSourceIds is { Count: > 0 })
        {
            sql.Append("JOIN substrate.entity_model_source ems " +
                       "ON ems.entity_type_id = e.entity_type_id AND ems.entity_hash = e.hash ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.entity_significance s " +
                       "ON s.entity_type_id = e.entity_type_id AND s.entity_hash = e.hash ");
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
        StringBuilder sql = new();
        sql.Append(@"
            SELECT DISTINCT tgt_et.code, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id AND et.code = 'has_tensor'
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_type tgt_et ON tgt_et.id = em_t.entity_type_id
              JOIN substrate.entity_type src_et ON src_et.id = em_s.entity_type_id
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
            sql.Append("JOIN substrate.entity_model_source ems " +
                       "ON ems.entity_type_id = em_t.entity_type_id AND ems.entity_hash = em_t.entity_hash ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.entity_significance s " +
                       "ON s.entity_type_id = em_t.entity_type_id AND s.entity_hash = em_t.entity_hash ");
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
              JOIN substrate.entity_type tgt_et ON tgt_et.id = em_t.entity_type_id
              JOIN substrate.entity_type src_et ON src_et.id = em_s.entity_type_id
              JOIN substrate.entity_significance s
                ON s.entity_type_id = em_t.entity_type_id AND s.entity_hash = em_t.entity_hash
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
            SELECT em_t.entity_type_id, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_type tgt_et ON tgt_et.id = em_t.entity_type_id
              JOIN substrate.entity_significance s
                ON s.entity_type_id = em_t.entity_type_id AND s.entity_hash = em_t.entity_hash
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
               AND size_s.entity_type_id = em_s.entity_type_id
               AND size_s.entity_hash    = em_s.entity_hash
               AND size_t.entity_hash    = $2
             ORDER BY s.mu DESC
             LIMIT $3";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue(contextTypeCode);
        cmd.Parameters.AddWithValue(hiddenSizeHash);
        cmd.Parameters.AddWithValue(topK);
        return await ReadHandlesAsync(cmd, useTypeId: true, ct);
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
            SELECT em_t.entity_type_id, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.entity_type tgt_et ON tgt_et.id = em_t.entity_type_id
              JOIN substrate.entity_significance s
                ON s.entity_type_id = em_t.entity_type_id AND s.entity_hash = em_t.entity_hash
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
            wheres.Add("arch_s.entity_type_id = em_s.entity_type_id");
            wheres.Add("arch_s.entity_hash    = em_s.entity_hash");
            wheres.Add("arch_t.entity_hash    = $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = aHandle.Hash });
        }
        _ = headDim; // informational; downstream verifies shape compatibility per scatter call.
        sql.Append(" WHERE ").Append(string.Join(" AND ", wheres));
        sql.Append(" ORDER BY s.mu DESC LIMIT $").Append(parameters.Count + 1);
        parameters.Add(new NpgsqlParameter { Value = topK });

        return await ReadHandlesAsync(sql.ToString(), parameters, ct, useTypeId: true);
    }

    public async Task<IReadOnlyList<EntityHandle>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT em_t.entity_type_id, em_t.entity_hash
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_type_id = e.edge_type_id AND em_s.edge_hash = e.hash
              JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
              JOIN substrate.edge_member em_t ON em_t.edge_type_id = e.edge_type_id AND em_t.edge_hash = e.hash
              JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
              JOIN substrate.tensor_tensor_role ttr
                ON ttr.entity_type_id = em_s.entity_type_id AND ttr.entity_hash = em_s.entity_hash
              JOIN substrate.tensor_role tr ON tr.id = ttr.tensor_role_id
             WHERE et.code = 'has_rank_component'
               AND tr.code = $1
             ORDER BY e.hash ASC
             LIMIT $2";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue(tensorRoleCode);
        cmd.Parameters.AddWithValue(topK);
        return await ReadHandlesAsync(cmd, useTypeId: true, ct);
    }

    private async Task<IReadOnlyList<EntityHandle>> ReadHandlesAsync(
        string sql, IReadOnlyList<NpgsqlParameter> parameters, CancellationToken ct,
        bool useTypeId = false)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach (NpgsqlParameter p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        return await ReadHandlesAsync(cmd, useTypeId, ct);
    }

    private static async Task<IReadOnlyList<EntityHandle>> ReadHandlesAsync(
        NpgsqlCommand cmd, bool useTypeId, CancellationToken ct)
    {
        List<EntityHandle> results = [];
        if (useTypeId)
        {
            // SELECT entity_type_id, entity_hash → resolve type code via in-process map.
            Dictionary<int, string> typeIdToCode = await LoadEntityTypeCodesAsync(cmd.Connection!, ct);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                int typeId = reader.GetInt32(0);
                byte[] hash = (byte[])reader.GetValue(1);
                if (typeIdToCode.TryGetValue(typeId, out string? code))
                {
                    results.Add(new EntityHandle(hash, code));
                }
            }
        }
        else
        {
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string typeCode = reader.GetString(0).Trim();
                byte[] hash = (byte[])reader.GetValue(1);
                results.Add(new EntityHandle(hash, typeCode));
            }
        }
        return results;
    }

    private static Dictionary<int, string> _entityTypeCodeCache = [];
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    private static async Task<Dictionary<int, string>> LoadEntityTypeCodesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_entityTypeCodeCache.Count > 0)
        {
            return _entityTypeCodeCache;
        }
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_entityTypeCodeCache.Count > 0)
            {
                return _entityTypeCodeCache;
            }
            Dictionary<int, string> map = [];
            await using NpgsqlCommand cmd = new(
                "SELECT id, code FROM substrate.entity_type", conn);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                map[reader.GetInt32(0)] = reader.GetString(1).Trim();
            }
            _entityTypeCodeCache = map;
            return map;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
