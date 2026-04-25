using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Engine.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Engine.Tests.Inference;

public sealed class SubstrateInferenceEngineTests
{
    private static SubstrateInferenceEngine CreateEngine(
        FakeTraversal? traversal = null,
        FakeEntityReader? entityReader = null)
    {
        return new SubstrateInferenceEngine(
            traversal ?? new FakeTraversal(),
            entityReader ?? new FakeEntityReader(),
            NullLogger<SubstrateInferenceEngine>.Instance);
    }

    [Fact]
    public async Task InferAsync_WithSeedIds_SkipsTextDecomposition()
    {
        FakeTraversal traversal = new();
        FakeEntityReader reader = new();
        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            SeedEntityIds = [100L, 200L],
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal([100L, 200L], result.SeedEntityIds);
        Assert.False(reader.FindEntitiesByContentCalled);
        Assert.True(traversal.TraverseCalled);
    }

    [Fact]
    public async Task InferAsync_WithText_ResolvesSeeds()
    {
        FakeEntityReader reader = new();
        reader.ContentMatches["hello"] = [(1L, "lemma")];
        reader.ContentMatches["world"] = [(2L, "word_form")];

        FakeTraversal traversal = new();
        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            Text = "Hello, World!",
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.True(reader.FindEntitiesByContentCalled);
        Assert.Contains(1L, result.SeedEntityIds);
        Assert.Contains(2L, result.SeedEntityIds);
    }

    [Fact]
    public async Task InferAsync_NoMatchingSeeds_ReturnsEmpty()
    {
        FakeEntityReader reader = new();
        SubstrateInferenceEngine engine = CreateEngine(entityReader: reader);

        InferenceQuery query = new()
        {
            Text = "xyzzy",
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Empty(result.Paths);
        Assert.Empty(result.SeedEntityIds);
        Assert.Equal(0, result.NodesVisited);
    }

    [Fact]
    public async Task InferAsync_PathSelection_ReturnsTopK()
    {
        FakeTraversal traversal = new();
        for (int i = 0; i < 20; i++)
        {
            traversal.Result.Paths.Add(new TraversalPath
            {
                Steps = [new TraversalStep { EntityId = i + 1 }],
                PathSignificance = i * 10.0,
            });
        }

        SubstrateInferenceEngine engine = CreateEngine(traversal);

        InferenceQuery query = new()
        {
            SeedEntityIds = [1L],
            ArenaCode = "lexical_disambiguation",
            MaxResults = 5,
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(5, result.Paths.Count);
        // Should be sorted descending by significance.
        Assert.True(result.Paths[0].PathSignificance >= result.Paths[1].PathSignificance);
        Assert.True(result.Paths[1].PathSignificance >= result.Paths[2].PathSignificance);
    }

    [Fact]
    public async Task InferAsync_GathersEntityMetadata()
    {
        FakeTraversal traversal = new();
        traversal.Result.Paths.Add(new TraversalPath
        {
            Steps =
            [
                new TraversalStep { EntityId = 10 },
                new TraversalStep { EntityId = 20 },
            ],
            PathSignificance = 100.0,
        });

        FakeEntityReader reader = new();
        reader.EntityInfoMap[10] = new EntityInfo
        {
            EntityTypeCode = "lemma",
            Hash = [1, 2, 3],
        };
        reader.EntityInfoMap[20] = new EntityInfo
        {
            EntityTypeCode = "synset",
            Hash = [4, 5, 6],
        };

        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            SeedEntityIds = [10L],
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("lemma", result.Entities[10].EntityTypeCode);
        Assert.Equal("synset", result.Entities[20].EntityTypeCode);
    }

    [Fact]
    public async Task InferAsync_FewerPathsThanMaxResults_ReturnsAll()
    {
        FakeTraversal traversal = new();
        traversal.Result.Paths.Add(new TraversalPath
        {
            Steps = [new TraversalStep { EntityId = 1 }],
            PathSignificance = 50.0,
        });

        SubstrateInferenceEngine engine = CreateEngine(traversal);

        InferenceQuery query = new()
        {
            SeedEntityIds = [1L],
            ArenaCode = "lexical_disambiguation",
            MaxResults = 100,
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Single(result.Paths);
    }

    [Fact]
    public async Task InferAsync_TextTokenization_SplitsPunctuation()
    {
        FakeEntityReader reader = new();
        reader.ContentMatches["don"] = [(10L, "lemma")];
        reader.ContentMatches["t"] = [(11L, "lemma")];
        reader.ContentMatches["stop"] = [(12L, "lemma")];

        SubstrateInferenceEngine engine = CreateEngine(entityReader: reader);

        InferenceQuery query = new()
        {
            Text = "Don't stop!",
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Contains(10L, result.SeedEntityIds);
        Assert.Contains(12L, result.SeedEntityIds);
    }

    [Fact]
    public async Task InferAsync_DuplicateTokens_DeduplicatedInResolution()
    {
        FakeEntityReader reader = new();
        reader.ContentMatches["the"] = [(1L, "lemma")];

        SubstrateInferenceEngine engine = CreateEngine(entityReader: reader);

        InferenceQuery query = new()
        {
            Text = "The the THE",
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        // "the" should be resolved once (case-insensitive dedup), but the
        // entity may appear once per match.
        Assert.Contains(1L, result.SeedEntityIds);
        Assert.True(reader.FindCallCount <= 1,
            "Duplicate tokens should be deduplicated before resolution");
    }

    [Fact]
    public async Task InferAsync_PassesTraversalParameters()
    {
        FakeTraversal traversal = new();
        SubstrateInferenceEngine engine = CreateEngine(traversal);

        InferenceQuery query = new()
        {
            SeedEntityIds = [42L],
            MaxDepth = 3,
            SignificanceThreshold = 1200.0,
            CostBudget = 5000.0,
            ArenaCode = "translation_quality",
            EdgeTypeFilter = ["has_sense", "has_lemma"],
        };

        await engine.InferAsync(query, CancellationToken.None);

        Assert.NotNull(traversal.LastQuery);
        Assert.Equal(3, traversal.LastQuery.MaxDepth);
        Assert.Equal(1200.0, traversal.LastQuery.SignificanceThreshold);
        Assert.Equal(5000.0, traversal.LastQuery.CostBudget);
        Assert.Equal("translation_quality", traversal.LastQuery.ArenaCode);
        Assert.Equal(["has_sense", "has_lemma"], traversal.LastQuery.EdgeTypeFilter);
    }

    [Fact]
    public async Task InferAsync_RecordsElapsed()
    {
        SubstrateInferenceEngine engine = CreateEngine();

        InferenceQuery query = new()
        {
            SeedEntityIds = [1L],
            ArenaCode = "lexical_disambiguation",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    // ── Fakes ──

    internal sealed class FakeTraversal : ITraversal
    {
        public FakeTraversalResult Result { get; } = new();
        public bool TraverseCalled { get; private set; }
        public TraversalQuery? LastQuery { get; private set; }

        public Task<TraversalResult> TraverseAsync(TraversalQuery query, CancellationToken ct)
        {
            TraverseCalled = true;
            LastQuery = query;
            return Task.FromResult<TraversalResult>(new TraversalResult
            {
                Paths = Result.Paths,
                NodesVisited = Result.Paths.Sum(p => p.Steps.Count),
                TotalCost = Result.Paths.Sum(p => 1.0 / Math.Max(p.PathSignificance, 0.001)),
                Elapsed = TimeSpan.FromMilliseconds(1),
            });
        }
    }

    internal sealed class FakeTraversalResult
    {
        public List<TraversalPath> Paths { get; } = [];
    }

    internal sealed class FakeEntityReader : IEntityReader
    {
        public Dictionary<string, List<(long EntityId, string EntityTypeCode)>> ContentMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, EntityInfo> EntityInfoMap { get; } = [];
        public Dictionary<long, List<(long ChildEntityId, int Position)>> SequenceChildren { get; } = [];
        public bool FindEntitiesByContentCalled { get; private set; }
        public int FindCallCount { get; private set; }

        public Task<IReadOnlyDictionary<byte[], long>> ResolveEntityIdsAsync(
            IReadOnlyList<byte[]> hashes, CancellationToken ct)
        {
            IReadOnlyDictionary<byte[], long> empty = new Dictionary<byte[], long>();
            return Task.FromResult(empty);
        }

        public Task<IReadOnlyDictionary<long, EntityInfo>> GetEntityInfoAsync(
            IReadOnlyList<long> entityIds, CancellationToken ct)
        {
            Dictionary<long, EntityInfo> result = [];
            foreach (long id in entityIds)
            {
                if (EntityInfoMap.TryGetValue(id, out EntityInfo? info))
                {
                    result[id] = info;
                }
            }
            return Task.FromResult<IReadOnlyDictionary<long, EntityInfo>>(result);
        }

        public Task<IReadOnlyList<(long ChildEntityId, int Position)>> GetSequenceChildrenAsync(
            long parentEntityId, CancellationToken ct)
        {
            if (SequenceChildren.TryGetValue(parentEntityId, out var children))
            {
                return Task.FromResult<IReadOnlyList<(long, int)>>(children);
            }
            return Task.FromResult<IReadOnlyList<(long, int)>>(Array.Empty<(long, int)>());
        }

        public Task<IReadOnlyDictionary<long, EdgeInfo>> GetEdgeInfoAsync(
            IReadOnlyList<long> edgeIds, CancellationToken ct)
        {
            IReadOnlyDictionary<long, EdgeInfo> empty = new Dictionary<long, EdgeInfo>();
            return Task.FromResult(empty);
        }

        public Task<IReadOnlyList<(long EntityId, string EntityTypeCode)>> FindEntitiesByContentAsync(
            string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
        {
            FindEntitiesByContentCalled = true;
            FindCallCount++;
            if (ContentMatches.TryGetValue(content, out var matches))
            {
                return Task.FromResult<IReadOnlyList<(long, string)>>(matches);
            }
            return Task.FromResult<IReadOnlyList<(long, string)>>(Array.Empty<(long, string)>());
        }

        public Task<IReadOnlyList<long>> GetOutboundEdgeTargetsAsync(
            long sourceEntityId, string edgeTypeCode, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        }
    }
}
