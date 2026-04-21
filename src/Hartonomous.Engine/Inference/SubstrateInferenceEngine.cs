using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Engine.Inference;

/// <summary>
/// Substrate inference engine. Decomposes a text query into seed entities,
/// activates them via significance-guided A* traversal, selects the top-k
/// paths, and enriches results with entity metadata.
///
/// Steps (per inference.md):
///   0. Prompt ingestion — decompose query text into substrate entities
///   1. Seed activation — resolve seeds to entity IDs
///   2. Significance-guided traversal — A* over typed edges with Glicko-2 cost
///   3. Path selection — rank and filter paths by significance
///   4. Composition assembly — enrich paths with entity metadata for recomposition
/// </summary>
public sealed partial class SubstrateInferenceEngine : IInferenceEngine
{
    private readonly ITraversal _traversal;
    private readonly IEntityReader _entityReader;
    private readonly ILogger<SubstrateInferenceEngine> _logger;

    /// <summary>
    /// Entity types that are valid seed targets for text queries.
    /// Queries are resolved against lemmas, word forms, and synsets.
    /// </summary>
    private static readonly string[] SeedEntityTypes =
        ["lemma", "word_form", "synset", "wikt_sense"];

    public SubstrateInferenceEngine(
        ITraversal traversal,
        IEntityReader entityReader,
        ILogger<SubstrateInferenceEngine> logger)
    {
        _traversal = traversal;
        _entityReader = entityReader;
        _logger = logger;
    }

    public async Task<InferenceResult> InferAsync(InferenceQuery query, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // Step 0–1: Resolve seeds.
        IReadOnlyList<long> seedIds = query.SeedEntityIds
            ?? (query.Text is not null
                ? await ResolveSeedsFromTextAsync(query.Text, ct)
                : []);

        if (seedIds.Count == 0)
        {
            LogNoSeedsResolved(_logger);
            return EmptyResult(seedIds, sw);
        }

        LogSeedActivation(_logger, seedIds.Count);

        // Step 2: Significance-guided traversal.
        TraversalQuery traversalQuery = new()
        {
            SeedEntityIds = seedIds,
            MaxDepth = query.MaxDepth,
            SignificanceThreshold = query.SignificanceThreshold,
            CostBudget = query.CostBudget,
            ArenaCode = query.ArenaCode,
            EdgeTypeFilter = query.EdgeTypeFilter,
        };

        TraversalResult traversalResult = await _traversal.TraverseAsync(traversalQuery, ct);

        LogTraversalComplete(
            _logger,
            traversalResult.Paths.Count,
            traversalResult.NodesVisited,
            traversalResult.Elapsed.TotalMilliseconds);

        // Step 3: Path selection — rank by significance, take top-k.
        IReadOnlyList<TraversalPath> selectedPaths = SelectPaths(
            traversalResult.Paths, query.MaxResults);

        // Step 4: Composition assembly — gather entity metadata for all entities in paths.
        IReadOnlyDictionary<long, EntityInfo> entities = await GatherEntityMetadataAsync(
            selectedPaths, ct);

        sw.Stop();

        return new InferenceResult
        {
            SeedEntityIds = seedIds,
            Paths = selectedPaths,
            Entities = entities,
            NodesVisited = traversalResult.NodesVisited,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Decompose a text query into seed entity IDs by tokenizing the input
    /// and resolving each token against existing substrate entities (lemmas,
    /// word forms, synsets) via content hash lookup.
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveSeedsFromTextAsync(
        string text, CancellationToken ct)
    {
        // Tokenize: split on whitespace and punctuation, deduplicate.
        HashSet<string> tokens = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split(
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 0)
            {
                tokens.Add(raw);
            }
        }

        HashSet<long> seedIds = [];

        // Resolve each token against the substrate entity table.
        foreach (string token in tokens)
        {
            IReadOnlyList<(long EntityId, string EntityTypeCode)> matches =
                await _entityReader.FindEntitiesByContentAsync(token, SeedEntityTypes, ct);

            foreach ((long entityId, string typeCode) in matches)
            {
                seedIds.Add(entityId);
                LogTokenResolved(_logger, token, entityId, typeCode);
            }
        }

        return [.. seedIds];
    }

    /// <summary>
    /// Select the top-k paths ranked by path significance (highest first).
    /// </summary>
    private static IReadOnlyList<TraversalPath> SelectPaths(
        IReadOnlyList<TraversalPath> paths, int maxResults)
    {
        if (paths.Count <= maxResults)
        {
            return paths;
        }

        // Sort descending by significance, take top-k. Stable sort for determinism.
        return paths
            .OrderByDescending(p => p.PathSignificance)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Gather entity metadata for all entities referenced in selected paths.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, EntityInfo>> GatherEntityMetadataAsync(
        IReadOnlyList<TraversalPath> paths, CancellationToken ct)
    {
        HashSet<long> entityIds = [];
        foreach (TraversalPath path in paths)
        {
            foreach (TraversalStep step in path.Steps)
            {
                entityIds.Add(step.EntityId);
            }
        }

        if (entityIds.Count == 0)
        {
            return new Dictionary<long, EntityInfo>();
        }

        return await _entityReader.GetEntityInfoAsync(entityIds.ToList(), ct);
    }

    private static InferenceResult EmptyResult(IReadOnlyList<long> seedIds, Stopwatch sw)
    {
        sw.Stop();
        return new InferenceResult
        {
            SeedEntityIds = seedIds,
            Paths = [],
            Entities = new Dictionary<long, EntityInfo>(),
            NodesVisited = 0,
            Elapsed = sw.Elapsed,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No seed entities resolved for query")]
    private static partial void LogNoSeedsResolved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Activated {SeedCount} seed entities")]
    private static partial void LogSeedActivation(ILogger logger, int seedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Traversal returned {PathCount} paths visiting {NodeCount} nodes in {ElapsedMs}ms")]
    private static partial void LogTraversalComplete(ILogger logger, int pathCount, int nodeCount, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved token '{Token}' → entity {EntityId} ({TypeCode})")]
    private static partial void LogTokenResolved(ILogger logger, string token, long entityId, string typeCode);
}
