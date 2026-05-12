using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Traversal;

/// <summary>
/// Hash-as-PK A* traversal backed by the PostgreSQL C extension. C# owns only
/// query shaping and handle projection; the substrate owns graph expansion.
/// </summary>
public sealed class NpgsqlTraversal : ITraversal
{
    private const int MaxResults = 1000;
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlTraversal(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TraversalResult> TraverseAsync(TraversalQuery query, CancellationToken ct)
    {
        if (query.Seeds.Count == 0)
        {
            return new TraversalResult
            {
                Paths = [],
                NodesVisited = 0,
                TotalCost = 0,
                Elapsed = TimeSpan.Zero,
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        List<TraversalPath> completedPaths = [];

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = TraversalSql.NativeAstar;
        cmd.Parameters.Add(new NpgsqlParameter("seed_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = query.Seeds.Select(seed => seed.Hash.ToByteArray()).ToArray(),
        });
        cmd.Parameters.Add(new NpgsqlParameter("seed_type_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = query.Seeds.Select(seed => seed.EntityTypeCode).ToArray(),
        });
        cmd.Parameters.Add(new NpgsqlParameter("edge_type_codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = query.EdgeTypeFilter is { Count: > 0 }
                ? query.EdgeTypeFilter.ToArray()
                : [],
        });
        cmd.Parameters.Add(new NpgsqlParameter("arena_code", NpgsqlDbType.Text)
        {
            Value = query.ArenaCode,
        });
        cmd.Parameters.Add(new NpgsqlParameter("max_depth", NpgsqlDbType.Integer)
        {
            Value = query.MaxDepth,
        });
        cmd.Parameters.Add(new NpgsqlParameter("max_results", NpgsqlDbType.Integer)
        {
            Value = MaxResults,
        });
        cmd.Parameters.Add(new NpgsqlParameter("min_mu", NpgsqlDbType.Double)
        {
            Value = query.SignificanceThreshold > 0
                ? query.SignificanceThreshold
                : DBNull.Value,
        });
        bool costBudgetIsUnbounded = double.IsPositiveInfinity(query.CostBudget);
        cmd.Parameters.Add(new NpgsqlParameter("cost_budget_is_unbounded", NpgsqlDbType.Boolean)
        {
            Value = costBudgetIsUnbounded,
        });
        cmd.Parameters.Add(new NpgsqlParameter("cost_budget", NpgsqlDbType.Double)
        {
            Value = costBudgetIsUnbounded ? 0.0 : query.CostBudget,
        });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            TraversalPath? path = ReadPath(reader);
            if (path is not null)
            {
                completedPaths.Add(path);
            }
        }

        sw.Stop();

        double totalCost = 0;
        foreach (TraversalPath p in completedPaths)
        {
            if (p.PathSignificance > 0 && !double.IsPositiveInfinity(p.PathSignificance))
            {
                totalCost += 1.0 / p.PathSignificance;
            }
        }

        return new TraversalResult
        {
            Paths = completedPaths,
            NodesVisited = completedPaths.Count,
            TotalCost = totalCost,
            Elapsed = sw.Elapsed,
        };
    }

    private static TraversalPath? ReadPath(NpgsqlDataReader reader)
    {
        EntityHandle seed = new((byte[])reader.GetValue(0), reader.GetString(1).Trim());
        EntityHandle target = new((byte[])reader.GetValue(2), reader.GetString(3).Trim());
        double totalMu = reader.GetDouble(5);
        byte[][] edgeHashes = (byte[][])reader.GetValue(6);
        string[] edgeTypeCodes = (string[])reader.GetValue(7);

        TraversalStep targetStep = new()
        {
            Entity = target,
            Edge = edgeHashes.Length > 0 && edgeTypeCodes.Length > 0
                ? new EdgeHandle(edgeHashes[^1], edgeTypeCodes[^1].Trim())
                : null,
            EdgeMu = totalMu,
        };

        return new TraversalPath
        {
            Steps =
            [
                new TraversalStep { Entity = seed, Edge = null, EdgeMu = null },
                targetStep,
            ],
            PathSignificance = totalMu,
        };
    }
}
