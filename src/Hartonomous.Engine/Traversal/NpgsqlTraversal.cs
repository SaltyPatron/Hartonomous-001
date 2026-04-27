using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Traversal;

public sealed class NpgsqlTraversal : ITraversal
{
    private static readonly string[] DefaultTargetEntityTypeCodes =
        ["synset", "lemma", "wikt_sense", "word_form"];

    private readonly NpgsqlDataSource _dataSource;
    private readonly IReferenceDataReader _refReader;

    public NpgsqlTraversal(NpgsqlDataSource dataSource, IReferenceDataReader refReader)
    {
        _dataSource = dataSource;
        _refReader = refReader;
    }

    private static async Task<(List<TraversalPath> Paths, int NodesVisited)> TraverseOneOnConnectionAsync(
        NpgsqlConnection conn,
        long seedId, int targetTypeId, int arenaId, int maxDepth,
        int? edgeTypeFilter, double significanceThreshold, double costBudget,
        CancellationToken ct)
    {
        List<TraversalPath> paths = new();
        int nodesVisited = 0;

        await using NpgsqlCommand cmd = new(
            "SELECT target_entity_id, cost, path, edge_path " +
            "FROM traverse_astar($1, $2, $3, $4, $5, $6, $7)", conn);
        // Timeout cap is a safety net for pathological inputs (cycle-rich subgraphs,
        // bad seeds), not a normal-case allowance — traverse_astar should return in
        // milliseconds for typical depth-3-to-6 queries against the indexed graph.
        cmd.CommandTimeout = 300;

        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, seedId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, targetTypeId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, arenaId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, maxDepth);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, 100); // max_results
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer,
            (object?)edgeTypeFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Double,
            significanceThreshold > 0 ? significanceThreshold : DBNull.Value);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            double cost = reader.GetDouble(1);
            if (cost > costBudget)
            {
                continue;
            }
            long[] entityPath = (long[])reader.GetValue(2);
            long[] edgePath = reader.IsDBNull(3)
                ? Array.Empty<long>()
                : (long[])reader.GetValue(3);

            List<TraversalStep> steps = new(entityPath.Length);
            for (int i = 0; i < entityPath.Length; i++)
            {
                steps.Add(new TraversalStep
                {
                    EntityId = entityPath[i],
                    EdgeId = i < edgePath.Length ? edgePath[i] : null,
                });
            }

            paths.Add(new TraversalPath
            {
                Steps = steps,
                PathSignificance = cost > 0 ? 1.0 / cost : double.MaxValue,
            });
            nodesVisited += entityPath.Length;
        }

        return (paths, nodesVisited);
    }

    public async Task<TraversalResult> TraverseAsync(TraversalQuery query, CancellationToken ct)
    {
        Dictionary<string, int> edgeTypes = await _refReader.LoadCodeMapAsync(
            "substrate.edge_type", 64, ct);
        Dictionary<string, int> sigContexts = await _refReader.LoadCodeMapAsync(
            "substrate.significance_context", 16, ct);
        Dictionary<string, int> entityTypes = await _refReader.LoadCodeMapAsync(
            "substrate.entity_type", 64, ct);

        if (!sigContexts.TryGetValue(query.ArenaCode, out int arenaId))
        {
            throw new InvalidOperationException(
                $"Unknown significance context: '{query.ArenaCode}'");
        }

        int? edgeTypeFilter = null;
        if (query.EdgeTypeFilter is { Count: 1 })
        {
            if (edgeTypes.TryGetValue(query.EdgeTypeFilter[0], out int etId))
            {
                edgeTypeFilter = etId;
            }
        }

        // The C-implemented traverse_astar requires a concrete target type per
        // call (target_type_id=0 is not a wildcard — it filters to no rows).
        // For unconstrained semantic queries, traverse against each canonical
        // semantic target type and union the results. The caller can narrow
        // via EdgeTypeFilter when a single-target traversal is wanted.
        int[] targetTypeIds = DefaultTargetEntityTypeCodes
            .Where(c => entityTypes.ContainsKey(c))
            .Select(c => entityTypes[c])
            .ToArray();
        if (targetTypeIds.Length == 0)
        {
            // Defensive fallback: use any single populated type id so the
            // traversal still issues at least one query rather than silently
            // returning empty.
            targetTypeIds = entityTypes.Values.Take(1).ToArray();
        }

        Stopwatch sw = Stopwatch.StartNew();

        // Issue every (seed × target_type) traverse_astar concurrently. The bulk-JOIN
        // fix in pg_traversal.c (one SPI per popped node returning neighbor + edge_mu
        // together) collapsed the per-traversal cost from ~80s to ~330ms; the
        // remaining cross-product is small (typically 1 seed × 4 default targets =
        // 4 calls) and parallelizes cleanly via the Npgsql connection pool. The
        // earlier per-call fresh-connection workaround was for an SPI-state SIGSEGV
        // that the bulk-JOIN refactor's reduced SPI churn also resolves.
        Task<(List<TraversalPath> Paths, int Nodes)>[] tasks =
            new Task<(List<TraversalPath>, int)>[query.SeedEntityIds.Count * targetTypeIds.Length];
        int taskIdx = 0;
        foreach (long seedId in query.SeedEntityIds)
        {
            foreach (int targetTypeId in targetTypeIds)
            {
                long sid = seedId;
                int ttid = targetTypeId;
                tasks[taskIdx++] = Task.Run(async () =>
                {
                    await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
                    return await TraverseOneOnConnectionAsync(
                        conn, sid, ttid, arenaId, query.MaxDepth,
                        edgeTypeFilter, query.SignificanceThreshold, query.CostBudget, ct);
                }, ct);
            }
        }

        (List<TraversalPath> Paths, int Nodes)[] results = await Task.WhenAll(tasks);
        List<TraversalPath> allPaths = [];
        int nodesVisited = 0;
        foreach ((List<TraversalPath> paths, int nodes) in results)
        {
            allPaths.AddRange(paths);
            nodesVisited += nodes;
        }

        sw.Stop();

        // Enrich steps with edge type codes and entity significance.
        await EnrichTraversalStepsAsync(allPaths, arenaId, ct);

        double totalCost = 0;
        foreach (TraversalPath p in allPaths)
        {
            totalCost += p.PathSignificance > 0 ? 1.0 / p.PathSignificance : 0;
        }

        return new TraversalResult
        {
            Paths = allPaths,
            NodesVisited = nodesVisited,
            TotalCost = totalCost,
            Elapsed = sw.Elapsed,
        };
    }

    private async Task EnrichTraversalStepsAsync(
        List<TraversalPath> paths, int arenaId, CancellationToken ct)
    {
        // Collect unique edge IDs and entity IDs across all paths.
        HashSet<long> edgeIdSet = [];
        HashSet<long> entityIdSet = [];
        foreach (TraversalPath p in paths)
        {
            foreach (TraversalStep step in p.Steps)
            {
                entityIdSet.Add(step.EntityId);
                if (step.EdgeId.HasValue)
                {
                    edgeIdSet.Add(step.EdgeId.Value);
                }
            }
        }

        if (edgeIdSet.Count == 0 && entityIdSet.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        // Batch lookup: edge_id → edge_type code.
        Dictionary<long, string> edgeTypeCodes = [];
        if (edgeIdSet.Count > 0)
        {
            long[] edgeIds = [.. edgeIdSet];
            await using NpgsqlCommand edgeCmd = new(
                "SELECT edge_id, edge_type_code FROM substrate.enrich_edges($1)", conn);
            edgeCmd.Parameters.AddWithValue(edgeIds);
            await using NpgsqlDataReader edgeReader = await edgeCmd.ExecuteReaderAsync(ct);
            while (await edgeReader.ReadAsync(ct))
            {
                edgeTypeCodes[edgeReader.GetInt64(0)] = edgeReader.GetString(1).Trim();
            }
        }

        // Batch lookup: entity_id → significance mu in the traversal arena.
        Dictionary<long, double> entityMus = [];
        if (entityIdSet.Count > 0)
        {
            long[] entityIds = [.. entityIdSet];
            await using NpgsqlCommand sigCmd = new(
                "SELECT entity_id, mu FROM substrate.enrich_significance($1, $2)", conn);
            sigCmd.Parameters.AddWithValue(entityIds);
            sigCmd.Parameters.AddWithValue(arenaId);
            await using NpgsqlDataReader sigReader = await sigCmd.ExecuteReaderAsync(ct);
            while (await sigReader.ReadAsync(ct))
            {
                entityMus[sigReader.GetInt64(0)] = sigReader.GetDouble(1);
            }
        }

        // Apply to steps.
        foreach (TraversalPath p in paths)
        {
            if (p.Steps is not List<TraversalStep> stepList)
            {
                continue;
            }
            for (int i = 0; i < stepList.Count; i++)
            {
                TraversalStep step = stepList[i];
                string? edgeCode = step.EdgeId.HasValue && edgeTypeCodes.TryGetValue(step.EdgeId.Value, out string? ec) ? ec : null;
                double? mu = entityMus.TryGetValue(step.EntityId, out double m) ? m : null;
                stepList[i] = step with { EdgeTypeCode = edgeCode, EdgeMu = mu };
            }
        }
    }
}
