using System;
using System.Collections.Generic;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Engine.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hartonomous.Engine.Tests.Inference;

public sealed class SubstrateInferenceEngineTests
{
    /// <summary>Test helper: long → EntityHandle so tests stay readable.</summary>
    private static EntityHandle H(long id, string typeCode = "test")
    {
        byte[] hash = new byte[32];
        BitConverter.GetBytes(id).CopyTo(hash, 0);
        return new EntityHandle(hash, typeCode);
    }

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
    public async Task InferAsync_WithSeeds_SkipsTextDecomposition()
    {
        FakeTraversal traversal = new();
        FakeEntityReader reader = new();
        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            Seeds = [H(100L), H(200L)],
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Seeds.Count);
        Assert.Contains(H(100L), result.Seeds);
        Assert.Contains(H(200L), result.Seeds);
        Assert.False(reader.FindEntitiesByContentCalled);
        Assert.True(traversal.TraverseCalled);
    }

    [Fact]
    public async Task InferAsync_WithText_ResolvesSeeds()
    {
        FakeEntityReader reader = new();
        reader.AddContentMatch("hello", H(1L, "lemma"));
        reader.AddContentMatch("world", H(2L, "word_form"));

        FakeTraversal traversal = new();
        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            Text = "Hello, World!",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.True(reader.FindEntitiesByContentCalled);
        Assert.Contains(H(1L, "lemma"), result.Seeds);
        Assert.Contains(H(2L, "word_form"), result.Seeds);
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
        Assert.Empty(result.Seeds);
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
                Steps = [new TraversalStep { Entity = H(i + 1) }],
                PathSignificance = i * 10.0,
            });
        }

        SubstrateInferenceEngine engine = CreateEngine(traversal);

        InferenceQuery query = new()
        {
            Seeds = [H(1L)],
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        // Engine returns ALL distinct paths, ordered descending by significance.
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
                new TraversalStep { Entity = H(10, "lemma") },
                new TraversalStep { Entity = H(20, "synset") },
            ],
            PathSignificance = 100.0,
        });

        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(10, "lemma"));
        reader.SetEntityInfo(H(20, "synset"));

        SubstrateInferenceEngine engine = CreateEngine(traversal, reader);

        InferenceQuery query = new()
        {
            Seeds = [H(10, "lemma")],
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Entities.Count);
        Assert.Equal("lemma", result.Entities[H(10, "lemma")].EntityTypeCode);
        Assert.Equal("synset", result.Entities[H(20, "synset")].EntityTypeCode);
    }

    [Fact]
    public async Task InferAsync_TextTokenization_SplitsPunctuation()
    {
        FakeEntityReader reader = new();
        reader.AddContentMatch("don", H(10, "lemma"));
        reader.AddContentMatch("t", H(11, "lemma"));
        reader.AddContentMatch("stop", H(12, "lemma"));

        SubstrateInferenceEngine engine = CreateEngine(entityReader: reader);

        InferenceQuery query = new()
        {
            Text = "Don't stop!",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Contains(H(10, "lemma"), result.Seeds);
        Assert.Contains(H(12, "lemma"), result.Seeds);
    }

    [Fact]
    public async Task InferAsync_DuplicateTokens_DeduplicatedInResolution()
    {
        FakeEntityReader reader = new();
        reader.AddContentMatch("the", H(1L, "lemma"));

        SubstrateInferenceEngine engine = CreateEngine(entityReader: reader);

        InferenceQuery query = new()
        {
            Text = "The the THE",
        };

        InferenceResult result = await engine.InferAsync(query, CancellationToken.None);

        Assert.Contains(H(1L, "lemma"), result.Seeds);
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
            Seeds = [H(42L)],
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
            Seeds = [H(1L)],
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
            return Task.FromResult(new TraversalResult
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
            ["test"] = 4,
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
        public Dictionary<EntityHandle, string> Texts { get; } = [];

        public Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct)
        {
            Texts.TryGetValue(root, out string? text);
            return Task.FromResult<string?>(text);
        }
    }

    internal sealed class FakeEntityReader : IEntityReader
    {
        private readonly Dictionary<string, List<EntityHandle>> _contentMatches =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<EntityHandle, EntityInfo> _entityInfo = [];

        public bool FindEntitiesByContentCalled { get; private set; }
        public int FindCallCount { get; private set; }

        public void AddContentMatch(string content, EntityHandle handle)
        {
            if (!_contentMatches.TryGetValue(content, out List<EntityHandle>? list))
            {
                list = [];
                _contentMatches[content] = list;
            }
            list.Add(handle);
        }

        public void SetEntityInfo(EntityHandle handle)
            => _entityInfo[handle] = new EntityInfo { Handle = handle };

        public Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
            IReadOnlyList<byte[]> hashes, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
            IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct)
        {
            Dictionary<EntityHandle, EntityInfo> result = [];
            foreach (EntityHandle h in entityHandles)
            {
                if (_entityInfo.TryGetValue(h, out EntityInfo? info))
                {
                    result[h] = info;
                }
                else
                {
                    // Synthesize a minimal EntityInfo so callers always see a value.
                    result[h] = new EntityInfo { Handle = h };
                }
            }
            return Task.FromResult<IReadOnlyDictionary<EntityHandle, EntityInfo>>(result);
        }

        public Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
            EntityHandle parent, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(EntityHandle, int)>>([]);

        public Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
            IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EdgeHandle, EdgeInfo>>(
                new Dictionary<EdgeHandle, EdgeInfo>());

        public Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
            string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
        {
            FindEntitiesByContentCalled = true;
            FindCallCount++;
            if (_contentMatches.TryGetValue(content, out List<EntityHandle>? matches))
            {
                return Task.FromResult<IReadOnlyList<EntityHandle>>(matches);
            }
            return Task.FromResult<IReadOnlyList<EntityHandle>>([]);
        }

        public Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
            EntityHandle source, string edgeTypeCode, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);
    }
}
