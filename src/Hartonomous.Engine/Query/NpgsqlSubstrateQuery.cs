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
}
