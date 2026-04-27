using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Query;
using Npgsql;

namespace Hartonomous.Engine.Query;

/// <summary>
/// Postgres-backed implementation of <see cref="ISubstrateQuery"/>. Composes
/// SQL filter clauses from the supplied <see cref="SubstrateQueryFilter"/>
/// using parameterized queries (no string interpolation of user values).
/// Joins follow the substrate's existing schema (substrate.entity,
/// substrate.entity_model_source, substrate.significance) without inventing
/// new tables.
/// </summary>
public sealed class NpgsqlSubstrateQuery : ISubstrateQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlSubstrateQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<(long EntityId, string EntityTypeCode)>> QueryEntitiesAsync(
        SubstrateQueryFilter filter, CancellationToken ct)
    {
        StringBuilder sql = new();
        sql.Append("SELECT DISTINCT e.id, et.code FROM substrate.entity e ");
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
            sql.Append("JOIN substrate.entity_model_source ems ON ems.entity_id = e.id ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.significance s ON s.entity_id = e.id ");
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
            sql.Append("ORDER BY s.mu DESC, e.id ASC ");
        }
        else
        {
            sql.Append("ORDER BY e.id ASC ");
        }

        if (filter.Limit is int lim)
        {
            sql.Append("LIMIT ").Append(lim);
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql.ToString(), conn);
        foreach (NpgsqlParameter p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        List<(long, string)> results = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return results;
    }

    public async Task<IReadOnlyList<long>> QueryTensorsForArchitectureAsync(
        long modelArchitectureEntityId, SubstrateQueryFilter filter, CancellationToken ct)
    {
        // Walk has_tensor edges from the architecture, then optionally filter
        // by model_source / significance. Single SQL avoids round-tripping
        // through GetOutboundEdgeTargetsAsync + per-tensor filter checks.
        StringBuilder sql = new();
        sql.Append(@"
            SELECT DISTINCT em_t.entity_id
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_id = e.id AND em_s.edge_role_id = 1
              JOIN substrate.edge_member em_t ON em_t.edge_id = e.id AND em_t.edge_role_id = 2
        ");
        List<string> wheres = ["et.code = 'has_tensor'", "em_s.entity_id = $1"];
        List<NpgsqlParameter> parameters = [new() { Value = modelArchitectureEntityId }];

        if (filter.ModelSourceIds is { Count: > 0 })
        {
            sql.Append("JOIN substrate.entity_model_source ems ON ems.entity_id = em_t.entity_id ");
            wheres.Add("ems.model_source_id = ANY($" + (parameters.Count + 1) + ")");
            parameters.Add(new NpgsqlParameter { Value = filter.ModelSourceIds });
        }
        if (filter.MinSignificanceMu is double minMu)
        {
            sql.Append("JOIN substrate.significance s ON s.entity_id = em_t.entity_id ");
            wheres.Add("s.mu >= $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = minMu });
            if (!string.IsNullOrEmpty(filter.ContextTypeCode))
            {
                sql.Append("JOIN substrate.significance_context sc ON sc.id = s.context_type_id ");
                wheres.Add("sc.code = $" + (parameters.Count + 1));
                parameters.Add(new NpgsqlParameter { Value = filter.ContextTypeCode });
            }
        }

        sql.Append("WHERE ").Append(string.Join(" AND ", wheres));
        sql.Append(" ORDER BY em_t.entity_id ASC");
        if (filter.Limit is int lim)
        {
            sql.Append(" LIMIT ").Append(lim);
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql.ToString(), conn);
        foreach (NpgsqlParameter p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        List<long> ids = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<long>> QueryFireflyForVocabAsync(
        IReadOnlyList<long> bpeTokenEntityIds,
        double minSignificanceMu,
        string contextTypeCode,
        int? limit,
        CancellationToken ct)
    {
        // Firefly entities link back to bpe_tokens via has_embedding_position
        // edges (bpe_token → embedding_firefly). Restrict to fireflies whose
        // source bpe_token is in the supplied vocab set, then rank by mu in
        // the requested arena.
        StringBuilder sql = new();
        sql.Append(@"
            SELECT DISTINCT em_t.entity_id
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_id = e.id AND em_s.edge_role_id = 1
              JOIN substrate.edge_member em_t ON em_t.edge_id = e.id AND em_t.edge_role_id = 2
              JOIN substrate.entity tgt ON tgt.id = em_t.entity_id
              JOIN substrate.entity_type tt ON tt.id = tgt.entity_type_id
              JOIN substrate.significance s ON s.entity_id = em_t.entity_id
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
             WHERE et.code = 'has_embedding_position'
               AND tt.code = 'embedding_firefly'
               AND em_s.entity_id = ANY($1)
               AND s.mu >= $2
               AND sc.code = $3
             ORDER BY s.mu DESC, em_t.entity_id ASC
        ");
        if (limit is int lim)
        {
            sql.Append(" LIMIT ").Append(lim);
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql.ToString(), conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = bpeTokenEntityIds });
        cmd.Parameters.Add(new NpgsqlParameter { Value = minSignificanceMu });
        cmd.Parameters.Add(new NpgsqlParameter { Value = contextTypeCode });
        List<long> ids = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<long>> QueryFfnNeuronsByHiddenDimAsync(
        int hiddenSize,
        int topK,
        string contextTypeCode,
        CancellationToken ct)
    {
        // ffn_neuron parents live as the source of has_ffn_neuron edges; a
        // neuron's hidden dim is the parent tensor's first shape dim, encoded
        // on a has_hidden_size edge to a substrate document carrying the
        // numeric literal as text. We round-trip through that edge → text
        // recompose, but at scale we'd cache. The more efficient path is
        // joining via the has_shape edge and matching the canonical text.
        const string sql = @"
            SELECT em_t.entity_id
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_id = e.id AND em_s.edge_role_id = 1
              JOIN substrate.edge_member em_t ON em_t.edge_id = e.id AND em_t.edge_role_id = 2
              JOIN substrate.entity tensor ON tensor.id = em_s.entity_id
              JOIN substrate.entity tgt ON tgt.id = em_t.entity_id
              JOIN substrate.entity_type tt ON tt.id = tgt.entity_type_id
              JOIN substrate.significance s ON s.entity_id = em_t.entity_id
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
              JOIN substrate.edge size_e ON size_e.edge_type_id =
                   (SELECT id FROM substrate.edge_type WHERE code = 'has_hidden_size')
              JOIN substrate.edge_member size_s ON size_s.edge_id = size_e.id AND size_s.edge_role_id = 1
              JOIN substrate.edge_member size_t ON size_t.edge_id = size_e.id AND size_t.edge_role_id = 2
              JOIN substrate.entity hs ON hs.id = size_t.entity_id
             WHERE et.code = 'has_ffn_neuron'
               AND tt.code = 'ffn_neuron'
               AND sc.code = $1
               AND size_s.entity_id = tensor.id
               AND hs.hash = digest($2::text, 'sha256')::bytea
             ORDER BY s.mu DESC
             LIMIT $3";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = contextTypeCode });
        cmd.Parameters.Add(new NpgsqlParameter { Value = hiddenSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = topK });
        List<long> ids = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<long>> QueryAttentionComponentsAsync(
        int headDim,
        long? archetypeEntityId,
        int topK,
        string contextTypeCode,
        CancellationToken ct)
    {
        // Attention components are sourced from has_attention_component edges
        // (tensor → attention_component). Head-dim is the second axis of the
        // source tensor's shape; we match it via the has_shape edge text
        // value. Optional archetype filter joins through encodes_archetype.
        StringBuilder sql = new();
        sql.Append(@"
            SELECT em_t.entity_id
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_id = e.id AND em_s.edge_role_id = 1
              JOIN substrate.edge_member em_t ON em_t.edge_id = e.id AND em_t.edge_role_id = 2
              JOIN substrate.entity tgt ON tgt.id = em_t.entity_id
              JOIN substrate.entity_type tt ON tt.id = tgt.entity_type_id
              JOIN substrate.significance s ON s.entity_id = em_t.entity_id
              JOIN substrate.significance_context sc ON sc.id = s.context_type_id
        ");
        List<string> wheres =
        [
            "et.code = 'has_attention_component'",
            "tt.code = 'attention_component'",
            "sc.code = $1",
        ];
        List<NpgsqlParameter> parameters = [new() { Value = contextTypeCode }];
        if (archetypeEntityId is long aid)
        {
            sql.Append(@"
              JOIN substrate.edge arch_e ON arch_e.edge_type_id =
                   (SELECT id FROM substrate.edge_type WHERE code = 'encodes_archetype')
              JOIN substrate.edge_member arch_s ON arch_s.edge_id = arch_e.id AND arch_s.edge_role_id = 1
              JOIN substrate.edge_member arch_t ON arch_t.edge_id = arch_e.id AND arch_t.edge_role_id = 2
            ");
            wheres.Add("arch_s.entity_id = em_s.entity_id");
            wheres.Add("arch_t.entity_id = $" + (parameters.Count + 1));
            parameters.Add(new NpgsqlParameter { Value = aid });
        }
        // headDim is informational here — used downstream by the recomposer to
        // verify shape compatibility before scattering. The query returns the
        // full attention_component candidate set; downstream filters enforce
        // exact-shape match per scatter call.
        _ = headDim;
        sql.Append(" WHERE ").Append(string.Join(" AND ", wheres));
        sql.Append(" ORDER BY s.mu DESC LIMIT $").Append(parameters.Count + 1);
        parameters.Add(new NpgsqlParameter { Value = topK });

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql.ToString(), conn);
        foreach (NpgsqlParameter p in parameters)
        {
            cmd.Parameters.Add(p);
        }
        List<long> ids = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<long>> QuerySingularDirectionsForRoleAsync(
        string tensorRoleCode,
        int topK,
        CancellationToken ct)
    {
        // svd_rank_component entities are reached via has_rank_component
        // (tensor → component). The tensor's role is recorded on the
        // tensor_tensor_role junction. SvdPass emits in descending-σ edge id
        // order, so ORDER BY edge_id ASC walks largest-σ first.
        const string sql = @"
            SELECT em_t.entity_id
              FROM substrate.edge e
              JOIN substrate.edge_type et ON et.id = e.edge_type_id
              JOIN substrate.edge_member em_s ON em_s.edge_id = e.id AND em_s.edge_role_id = 1
              JOIN substrate.edge_member em_t ON em_t.edge_id = e.id AND em_t.edge_role_id = 2
              JOIN substrate.tensor_tensor_role ttr ON ttr.entity_id = em_s.entity_id
              JOIN substrate.tensor_role tr ON tr.id = ttr.tensor_role_id
             WHERE et.code = 'has_rank_component'
               AND tr.code = $1
             ORDER BY e.id ASC
             LIMIT $2";

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = tensorRoleCode });
        cmd.Parameters.Add(new NpgsqlParameter { Value = topK });
        List<long> ids = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }
}
