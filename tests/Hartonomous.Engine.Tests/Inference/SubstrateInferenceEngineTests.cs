using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Engine.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Engine.Tests.Inference;

public sealed class SubstrateInferenceEngineTests
{
    private static SubstrateInferenceEngine CreateEngine(
        FakeTraversal? traversal = null,
        FakeEntityReader? entityReader = null,
        FakeReferenceData? referenceData = null,
        FakeTextRecompositionReader? textReader = null)
    {
        return new SubstrateInferenceEngine(
            traversal ?? new FakeTraversal(),
            entityReader ?? new FakeEntityReader(),
            referenceData ?? new FakeReferenceData(),
            NullLogger<SubstrateInferenceEngine>.Instance,
            textReader);
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
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Empty(result.Paths);
        Assert.Empty(result.SeedEntityIds);
        Assert.Equal(0, result.NodesVisited);
    }

    [Fact]
    public async Task InferAsync_PathSelection_DeduplicatesAndSortsBySignificance()
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
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        // Per the substrate-as-AI invention the engine returns ALL distinct
        // paths, ordered descending by composite significance — no caller cap.
        Assert.Equal(20, result.Paths.Count);
        for (int i = 1; i < result.Paths.Count; i++)
        {
            Assert.True(result.Paths[i - 1].PathSignificance >= result.Paths[i].PathSignificance);
        }
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
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("lemma", result.Entities[10].EntityTypeCode);
        Assert.Equal("synset", result.Entities[20].EntityTypeCode);
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
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Contains(1L, result.SeedEntityIds);
        // The engine emits both the original surface form and its lower-case
        // variant per token (case-preserving entities and case-folded entities
        // are both valid substrate matches), then deduplicates the union. For
        // "The the THE" the set is {"The", "the", "THE"} — 3 distinct lookups.
        Assert.True(reader.FindCallCount <= 3,
            "Duplicate surface tokens should collapse before resolution.");
    }

    [Fact]
    public async Task InferAsync_FansOutAcrossEveryArena()
    {
        FakeTraversal traversal = new();
        FakeReferenceData refs = new();
        refs.SignificanceContextCodes["lexical_disambiguation"] = 1;
        refs.SignificanceContextCodes["syntactic_role_fitness"] = 2;
        refs.SignificanceContextCodes["semantic_relevance"] = 3;

        SubstrateInferenceEngine engine = CreateEngine(traversal, referenceData: refs);

        InferenceQuery query = new()
        {
            SeedEntityIds = [42L],
        };

        await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(3, traversal.AllArenas.Count);
        Assert.Contains("lexical_disambiguation", traversal.AllArenas);
        Assert.Contains("syntactic_role_fitness", traversal.AllArenas);
        Assert.Contains("semantic_relevance", traversal.AllArenas);
    }

    [Fact]
    public async Task InferAsync_RecordsElapsed()
    {
        SubstrateInferenceEngine engine = CreateEngine();

        InferenceQuery query = new()
        {
            SeedEntityIds = [1L],
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
        public List<string> AllArenas { get; } = [];

        public Task<TraversalResult> TraverseAsync(TraversalQuery query, CancellationToken ct)
        {
            TraverseCalled = true;
            LastQuery = query;
            if (query.ArenaCode is not null)
            {
                AllArenas.Add(query.ArenaCode);
            }
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

    internal sealed class FakeReferenceData : IReferenceDataReader
    {
        public Dictionary<string, int> SignificanceContextCodes { get; } = new(StringComparer.Ordinal)
        {
            ["lexical_disambiguation"] = 1,
        };
        public Dictionary<string, int> EntityTypeCodes { get; } = new(StringComparer.Ordinal)
        {
            ["lemma"] = 1,
            ["word_form"] = 2,
            ["synset"] = 3,
        };

        public Task<Dictionary<string, int>> LoadCodeMapAsync(
            string tableName, int initialCapacity, CancellationToken ct)
        {
            Dictionary<string, int> result = tableName switch
            {
                "significance_context" => new Dictionary<string, int>(SignificanceContextCodes, StringComparer.Ordinal),
                "entity_type" => new Dictionary<string, int>(EntityTypeCodes, StringComparer.Ordinal),
                _ => new Dictionary<string, int>(StringComparer.Ordinal),
            };
            return Task.FromResult(result);
        }

        public Task<Dictionary<(string Key, string Value), int>> LoadKeyValueMapAsync(
            string tableName, string keyColumn, string valueColumn,
            int initialCapacity, CancellationToken ct)
            => Task.FromResult(new Dictionary<(string Key, string Value), int>());

        public Task<Dictionary<string, string>> LoadCodeTextMapAsync(
            string tableName, string valueColumn, int initialCapacity, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));

        public Task<HashSet<long>> LoadInt64SetAsync(
            string tableName, string columnName, CancellationToken ct)
            => Task.FromResult(new HashSet<long>());

        public Task<int> LoadIdByCodeAsync(
            string tableName, string code, CancellationToken ct)
            => Task.FromResult(0);

        public Task<Dictionary<byte[], byte[]>> LoadWordNetOffsetSynsetMapAsync(CancellationToken ct)
            => Task.FromResult(new Dictionary<byte[], byte[]>());
    }

    internal sealed class FakeTextRecompositionReader : ITextRecompositionReader
    {
        public Dictionary<long, string> Texts { get; } = [];

        public Task<string?> RecomposeTextAsync(long entityId, int maxDepth, CancellationToken ct)
        {
            Texts.TryGetValue(entityId, out string? text);
            return Task.FromResult<string?>(text);
        }
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
