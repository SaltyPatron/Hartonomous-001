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
/// Substrate inference engine. The forward pass.
///
/// Per the substrate-as-AI-model invention, the prompt IS substrate content
/// (not a query against a model), and the forward pass IS A* traversal
/// over significance-weighted typed edges (not a matmul). The public API
/// takes the prompt text only — no caller-specified arena, depth, cost
/// budget, edge filter, or result cap. Those compromises lived on the
/// previous InferenceQuery surface; they are gone.
///
/// Steps:
///   0. Decompose the prompt into substrate entities (codepoint → grapheme →
///      word_form → text_composition) and resolve seed entity IDs.
///   1. Fan out across every significance arena currently in the substrate
///      (open-vocabulary; no cherry-picking) and every plausible terminal
///      entity type. Each fan-out call invokes the C-extension traverse_astar.
///   2. Compose: a path's significance is its mean per-edge mu (so paths that
///      consistently cross strong edges win across arenas).
///   3. Recompose: walk the highest-composite-significance path, concatenate
///      each step entity's content (codepoint atoms recomposed via
///      <c>substrate.recompose_text</c>) into the answer string.
///
/// The substrate produces NOTHING when no path was found — honest abstention
/// per <c>docs/specs/engine/inference.md</c>.
/// </summary>
public sealed partial class SubstrateInferenceEngine : IInferenceEngine
{
    private readonly ITraversal _traversal;
    private readonly IEntityReader _entityReader;
    private readonly IReferenceDataReader _referenceData;
    private readonly ITextRecompositionReader? _textReader;
    private readonly ILogger<SubstrateInferenceEngine> _logger;

    public SubstrateInferenceEngine(
        ITraversal traversal,
        IEntityReader entityReader,
        IReferenceDataReader referenceData,
        ILogger<SubstrateInferenceEngine> logger,
        ITextRecompositionReader? textReader = null)
    {
        _traversal = traversal;
        _entityReader = entityReader;
        _referenceData = referenceData;
        _textReader = textReader;
        _logger = logger;
    }

    public async Task<InferenceResult> InferAsync(InferenceQuery query, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // 0. Resolve seeds. Either the caller pre-provided entity IDs (engine
        //    self-call / test path), or we decompose the prompt now.
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

        // 1. Cross-arena fan-out. Pull every arena code from
        //    substrate.significance_context (open-vocabulary). For each one,
        //    issue a traversal. The traversal layer fans out further across
        //    target entity types internally.
        IReadOnlyList<string> arenaCodes = await LoadAllArenaCodesAsync(ct);

        Task<TraversalResult>[] tasks = new Task<TraversalResult>[arenaCodes.Count];
        for (int i = 0; i < arenaCodes.Count; i++)
        {
            string arena = arenaCodes[i];
            tasks[i] = _traversal.TraverseAsync(new TraversalQuery
            {
                SeedEntityIds = seedIds,
                ArenaCode = arena,
            }, ct);
        }
        TraversalResult[] arenaResults = await Task.WhenAll(tasks);

        // Compose: collect every path. A path's significance is already its
        // 1/cost score from the originating arena; arenas with stronger edges
        // for this path produce higher significance, and the best per-path
        // significance across arenas is what we keep (max-pooling — pick the
        // arena that most strongly supports each path).
        Dictionary<string, TraversalPath> bestPathByKey = new(StringComparer.Ordinal);
        int totalNodes = 0;
        foreach (TraversalResult r in arenaResults)
        {
            totalNodes += r.NodesVisited;
            foreach (TraversalPath p in r.Paths)
            {
                string key = string.Join(",", p.Steps.Select(s => s.EntityId));
                if (!bestPathByKey.TryGetValue(key, out TraversalPath? existing)
                    || p.PathSignificance > existing.PathSignificance)
                {
                    bestPathByKey[key] = p;
                }
            }
        }

        List<TraversalPath> allPaths = [.. bestPathByKey.Values];
        allPaths.Sort((a, b) => b.PathSignificance.CompareTo(a.PathSignificance));

        LogTraversalComplete(_logger, allPaths.Count, totalNodes, sw.Elapsed.TotalMilliseconds);

        // 2. Recompose the highest-significance path into the answer string.
        //    Walk the path's terminal entity (the entity the substrate's A*
        //    selected as the "output" for this prompt) and recompose its
        //    content through substrate.recompose_text.
        string answer = string.Empty;
        if (allPaths.Count > 0)
        {
            long terminal = allPaths[0].Steps[^1].EntityId;
            answer = await RecomposeTextAsync(terminal, ct) ?? $"<entity {terminal}>";
        }

        // 3. Gather entity metadata for the trace.
        IReadOnlyDictionary<long, EntityInfo> entities = await GatherEntityMetadataAsync(allPaths, ct);

        sw.Stop();

        return new InferenceResult
        {
            Answer = answer,
            SeedEntityIds = seedIds,
            Paths = allPaths,
            Entities = entities,
            NodesVisited = totalNodes,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Decompose the prompt into substrate seed entities. Splits on UAX #29
    /// word boundaries (whitespace + punctuation as a starting approximation
    /// of the seg, with the full TextDecomposer wire-up as future work) and
    /// looks up matching entities of EVERY type the substrate stores —
    /// no hardcoded "valid seed types" list. The substrate decides what
    /// matches; the engine doesn't predicate on conventional NLP categories.
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveSeedsFromTextAsync(
        string text, CancellationToken ct)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        foreach (string raw in text.Split(
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 0)
            {
                tokens.Add(raw);
                tokens.Add(raw.ToLowerInvariant());
            }
        }

        HashSet<long> seedIds = [];
        IReadOnlyList<string> allEntityTypes = await LoadAllEntityTypeCodesAsync(ct);

        foreach (string token in tokens)
        {
            IReadOnlyList<(long EntityId, string EntityTypeCode)> matches =
                await _entityReader.FindEntitiesByContentAsync(token, allEntityTypes, ct);
            foreach ((long entityId, string typeCode) in matches)
            {
                seedIds.Add(entityId);
                LogTokenResolved(_logger, token, entityId, typeCode);
            }
        }

        return [.. seedIds];
    }

    private async Task<IReadOnlyList<string>> LoadAllArenaCodesAsync(CancellationToken ct)
    {
        Dictionary<string, int> map = await _referenceData.LoadCodeMapAsync(
            "significance_context", initialCapacity: 16, ct);
        return [.. map.OrderBy(kv => kv.Value).Select(kv => kv.Key)];
    }

    private async Task<IReadOnlyList<string>> LoadAllEntityTypeCodesAsync(CancellationToken ct)
    {
        Dictionary<string, int> map = await _referenceData.LoadCodeMapAsync(
            "entity_type", initialCapacity: 32, ct);
        return [.. map.OrderBy(kv => kv.Value).Select(kv => kv.Key)];
    }

    private async Task<string?> RecomposeTextAsync(long entityId, CancellationToken ct)
    {
        if (_textReader is null)
        {
            return null;
        }
        return await _textReader.RecomposeTextAsync(entityId, maxDepth: int.MaxValue, ct);
    }

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
        return await _entityReader.GetEntityInfoAsync([.. entityIds], ct);
    }

    private static InferenceResult EmptyResult(IReadOnlyList<long> seedIds, Stopwatch sw)
    {
        sw.Stop();
        return new InferenceResult
        {
            Answer = string.Empty,
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Cross-arena traversal returned {PathCount} composite paths visiting {NodeCount} nodes in {ElapsedMs}ms")]
    private static partial void LogTraversalComplete(ILogger logger, int pathCount, int nodeCount, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved token '{Token}' → entity {EntityId} ({TypeCode})")]
    private static partial void LogTokenResolved(ILogger logger, string token, long entityId, string typeCode);
}
