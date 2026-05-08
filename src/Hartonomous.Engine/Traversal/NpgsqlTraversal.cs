using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Traversal;

/// <summary>
/// Hash-as-PK A* traversal. Implements <see cref="ITraversal"/> in C# over
/// the substrate.entity_outbound_edges helper — no surrogate-id assumptions,
/// no dependency on the prior C-extension traverse_astar (which addressed
/// nodes by BIGINT and queried columns the post-refactor schema no longer
/// has).
///
/// The implementation is a textbook best-first A* with cost = sum of 1/mu
/// over edges along the path. Significance = 1 / total cost. Termination is
/// by max-depth, cost budget, or when every reachable node is exhausted.
/// Per-arena fan-out happens in <c>SubstrateInferenceEngine.InferAsync</c>;
/// each call to <see cref="TraverseAsync"/> walks under one arena code.
///
/// This is correctness-first. A server-side pl/pgsql A* is the natural
/// performance follow-up once the read-side migration is fully in place.
/// </summary>
public sealed class NpgsqlTraversal : ITraversal
{
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

        // edgeTypeFilter is a name-set; null = all edge types.
        HashSet<string>? edgeTypeFilter = query.EdgeTypeFilter is { Count: > 0 }
            ? new HashSet<string>(query.EdgeTypeFilter, StringComparer.Ordinal)
            : null;

        // Best-first A* from each seed. Paths share a global visit set so the
        // same node isn't expanded twice across seeds at higher cost.
        Dictionary<EntityHandle, double> bestCostByNode = new();
        List<TraversalPath> completedPaths = [];
        int nodesVisited = 0;

        PriorityQueue<AstarNode, double> heap = new();
        foreach (EntityHandle seed in query.Seeds)
        {
            AstarNode init = new(
                Entity: seed,
                Steps: [new TraversalStep { Entity = seed, Edge = null, EdgeMu = null }],
                Cost: 0.0,
                Depth: 0);
            heap.Enqueue(init, 0.0);
            bestCostByNode[seed] = 0.0;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        while (heap.Count > 0 && completedPaths.Count < 1000)
        {
            ct.ThrowIfCancellationRequested();
            AstarNode cur = heap.Dequeue();
            if (cur.Cost > query.CostBudget)
            {
                continue;
            }
            if (cur.Depth >= query.MaxDepth)
            {
                continue;
            }
            // Stale entry: a cheaper path to this node was already expanded.
            if (bestCostByNode.TryGetValue(cur.Entity, out double recordedCost)
                && recordedCost < cur.Cost)
            {
                continue;
            }
            nodesVisited++;

            // Record the path-so-far as a candidate result. The engine sorts
            // and selects later; the traversal returns every reached node.
            if (cur.Steps.Count > 1)
            {
                double sig = cur.Cost > 0 ? 1.0 / cur.Cost : double.MaxValue;
                completedPaths.Add(new TraversalPath
                {
                    Steps = cur.Steps,
                    PathSignificance = sig,
                });
            }

            // Expand neighbors via substrate.entity_neighbors (bidirectional —
            // returns co-members regardless of role direction so the A* can
            // follow forward edges, inverse edges, and arbitrary n-ary
            // relations in one walk).
            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.TraversalNeighbors,
                [
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = cur.Entity.Hash },
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = query.ArenaCode }
                ]);

            List<(EdgeHandle EdgeH, EntityHandle CoH, double EdgeMu)> neighbors = [];
            await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    string edgeTypeCode = reader.GetString(0).Trim();
                    if (edgeTypeFilter is not null && !edgeTypeFilter.Contains(edgeTypeCode))
                    {
                        continue;
                    }
                    byte[] edgeHash = (byte[])reader.GetValue(1);
                    string coTypeCode = reader.GetString(2).Trim();
                    byte[] coHash = (byte[])reader.GetValue(3);
                    double edgeMu = reader.GetDouble(4);

                    neighbors.Add((
                        new EdgeHandle(edgeHash, edgeTypeCode),
                        new EntityHandle(coHash, coTypeCode),
                        edgeMu));
                }
            }

            foreach ((EdgeHandle edgeH, EntityHandle coH, double edgeMu) in neighbors)
            {
                if (edgeMu < query.SignificanceThreshold)
                {
                    continue;
                }
                double stepCost = edgeMu > 0 ? 1.0 / edgeMu : double.PositiveInfinity;
                double newCost = cur.Cost + stepCost;
                if (newCost > query.CostBudget)
                {
                    continue;
                }
                if (bestCostByNode.TryGetValue(coH, out double prevCost) && prevCost <= newCost)
                {
                    continue;
                }
                bestCostByNode[coH] = newCost;

                List<TraversalStep> nextSteps = new(cur.Steps.Count + 1);
                nextSteps.AddRange(cur.Steps);
                nextSteps.Add(new TraversalStep
                {
                    Entity = coH,
                    Edge = edgeH,
                    EdgeMu = edgeMu,
                });

                AstarNode nextNode = new(
                    Entity: coH,
                    Steps: nextSteps,
                    Cost: newCost,
                    Depth: cur.Depth + 1);
                heap.Enqueue(nextNode, newCost);
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
            NodesVisited = nodesVisited,
            TotalCost = totalCost,
            Elapsed = sw.Elapsed,
        };
    }

    private readonly record struct AstarNode(
        EntityHandle Entity,
        IReadOnlyList<TraversalStep> Steps,
        double Cost,
        int Depth);
}
