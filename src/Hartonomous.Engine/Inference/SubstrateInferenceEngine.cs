using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Engine.Inference;

/// <summary>
/// Substrate inference engine. The forward pass.
///
/// Per the substrate-as-AI invention, the prompt IS substrate content (not a
/// query against a model), and the forward pass IS A* traversal over
/// significance-weighted typed edges (not a matmul). The public API takes
/// the prompt text only — no caller-specified arena, depth, cost budget,
/// edge filter, or result cap. Hash-as-PK throughout: every entity reference
/// is a composite (type_code, hash) handle.
///
/// Steps:
///   0. Decompose the prompt into substrate entities (codepoint → grapheme →
///      word_form → text_composition) and resolve seed entity handles.
///   1. Fan out across every significance arena currently in the substrate
///      (open-vocabulary; no cherry-picking) and traverse via A*.
///   2. Compose: each path's significance is its 1/cost score from the
///      originating arena; arenas with stronger edges for this path produce
///      higher significance, and the best per-path significance across arenas
///      is what we keep (max-pooling — pick the arena that most strongly
///      supports each path).
///   3. Recompose: walk the highest-composite-significance path's terminal
///      entity and recompose its content via substrate.recompose_text.
///
/// The substrate produces NOTHING when no path was found — honest abstention
/// per docs/specs/engine/inference.md.
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

        // 0. Resolve seeds.
        IReadOnlyList<EntityHandle> seeds = query.Seeds
            ?? (query.Text is not null
                ? await ResolveSeedsFromTextAsync(query.Text, ct)
                : []);

        if (seeds.Count == 0)
        {
            LogNoSeedsResolved(_logger);
            return EmptyResult(seeds, sw);
        }
        LogSeedActivation(_logger, seeds.Count);

        // 1. Cross-arena fan-out.
        IReadOnlyList<string> arenaCodes = await LoadAllArenaCodesAsync(ct);

        Task<TraversalResult>[] tasks = new Task<TraversalResult>[arenaCodes.Count];
        for (int i = 0; i < arenaCodes.Count; i++)
        {
            string arena = arenaCodes[i];
            tasks[i] = _traversal.TraverseAsync(new TraversalQuery
            {
                Seeds = seeds,
                ArenaCode = arena,
            }, ct);
        }
        TraversalResult[] arenaResults = await Task.WhenAll(tasks);

        // Compose: collect every path. Key by entity handle sequence; max-pool
        // significance across arenas.
        Dictionary<string, TraversalPath> bestPathByKey = new(StringComparer.Ordinal);
        int totalNodes = 0;
        foreach (TraversalResult r in arenaResults)
        {
            totalNodes += r.NodesVisited;
            foreach (TraversalPath p in r.Paths)
            {
                string key = string.Join(
                    ",",
                    p.Steps.Select(s => $"{s.Entity.EntityTypeCode}:{Convert.ToHexString(s.Entity.Hash)}"));
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

        // 2. Recompose the highest-significance path's terminal entity.
        string answer = string.Empty;
        if (allPaths.Count > 0)
        {
            EntityHandle terminal = allPaths[0].Steps[^1].Entity;
            answer = await RecomposeTextAsync(terminal, ct) ?? $"<entity {terminal}>";
        }

        // 3. Gather entity metadata for the trace.
        IReadOnlyDictionary<EntityHandle, EntityInfo> entities =
            await GatherEntityMetadataAsync(allPaths, ct);

        sw.Stop();

        return new InferenceResult
        {
            Answer = answer,
            Seeds = seeds,
            Paths = allPaths,
            Entities = entities,
            NodesVisited = totalNodes,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Decompose the prompt into substrate seed entity handles. Splits on
    /// punctuation/whitespace and looks up matching entities of every type
    /// the substrate stores — the substrate decides what matches.
    /// </summary>
    private async Task<IReadOnlyList<EntityHandle>> ResolveSeedsFromTextAsync(
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

        HashSet<EntityHandle> seeds = [];
        IReadOnlyList<string> allEntityTypes = await LoadAllEntityTypeCodesAsync(ct);

        foreach (string token in tokens)
        {
            IReadOnlyList<EntityHandle> matches =
                await _entityReader.FindEntitiesByContentAsync(token, allEntityTypes, ct);
            foreach (EntityHandle h in matches)
            {
                seeds.Add(h);
                LogTokenResolved(_logger, token, h.EntityTypeCode);
            }
        }

        return [.. seeds];
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

    private async Task<string?> RecomposeTextAsync(EntityHandle root, CancellationToken ct)
    {
        if (_textReader is null)
        {
            return null;
        }
        return await _textReader.RecomposeTextAsync(root, maxDepth: int.MaxValue, ct);
    }

    private async Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GatherEntityMetadataAsync(
        IReadOnlyList<TraversalPath> paths, CancellationToken ct)
    {
        HashSet<EntityHandle> handles = [];
        foreach (TraversalPath path in paths)
        {
            foreach (TraversalStep step in path.Steps)
            {
                handles.Add(step.Entity);
            }
        }
        if (handles.Count == 0)
        {
            return new Dictionary<EntityHandle, EntityInfo>();
        }
        return await _entityReader.GetEntityInfoAsync([.. handles], ct);
    }

    private static InferenceResult EmptyResult(IReadOnlyList<EntityHandle> seeds, Stopwatch sw)
    {
        sw.Stop();
        return new InferenceResult
        {
            Answer = string.Empty,
            Seeds = seeds,
            Paths = [],
            Entities = new Dictionary<EntityHandle, EntityInfo>(),
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved token '{Token}' → entity (type={TypeCode})")]
    private static partial void LogTokenResolved(ILogger logger, string token, string typeCode);
}
