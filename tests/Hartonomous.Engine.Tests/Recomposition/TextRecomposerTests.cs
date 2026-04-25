using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Recomposition;
using Hartonomous.Recomposers;

namespace Hartonomous.Engine.Tests.Recomposition;

public sealed class TextRecomposerTests
{
    private static TextRecomposer CreateRecomposer(FakeEntityReader? reader = null)
    {
        return new TextRecomposer(reader ?? new FakeEntityReader());
    }

    [Fact]
    public void OutputModality_IsText()
    {
        TextRecomposer recomposer = CreateRecomposer();
        Assert.Equal(Modality.Text, recomposer.OutputModality);
    }

    [Fact]
    public async Task RecomposeAsync_SingleAtom_ReturnsContentLabel()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "hello",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            1L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task RecomposeAsync_CompositionWithChildren_ConcatenatesAtoms()
    {
        FakeEntityReader reader = new();
        // Parent composition.
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [10],
        };
        reader.SequenceChildren[100] =
        [
            (1L, 0),
            (10L, 1),
            (2L, 2),
            (11L, 3),
            (3L, 4),
        ];
        // Child atoms are concatenated exactly as sequenced.
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "the",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [2],
            ContentLabel = "cat",
        };
        reader.EntityInfoMap[3] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [3],
            ContentLabel = "sat",
        };
        reader.EntityInfoMap[10] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [10],
            ContentLabel = " ",
        };
        reader.EntityInfoMap[11] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [11],
            ContentLabel = " ",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("the cat sat", result);
    }

    [Fact]
    public async Task RecomposeAsync_CodepointAtoms_ConcatenatedDirectly()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [10],
        };
        reader.SequenceChildren[100] =
        [
            (1L, 0),
            (2L, 1),
            (3L, 2),
        ];
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [1],
            ContentLabel = "c",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [2],
            ContentLabel = "a",
        };
        reader.EntityInfoMap[3] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [3],
            ContentLabel = "t",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("cat", result);
    }

    [Fact]
    public async Task RecomposeAsync_NestedComposition_RecursesDepthFirst()
    {
        FakeEntityReader reader = new();
        // Root composition.
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "paragraph",
            Hash = [10],
        };
        reader.SequenceChildren[100] = [(200L, 0), (30L, 1), (300L, 2)];

        // First sub-composition.
        reader.EntityInfoMap[200] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [20],
        };
        reader.SequenceChildren[200] = [(1L, 0), (10L, 1), (2L, 2)];

        // Second sub-composition.
        reader.EntityInfoMap[300] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [30],
        };
        reader.SequenceChildren[300] = [(3L, 0)];

        // Leaf atoms.
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "good",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [2],
            ContentLabel = "morning",
        };
        reader.EntityInfoMap[3] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [3],
            ContentLabel = "world",
        };
        reader.EntityInfoMap[10] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [4],
            ContentLabel = " ",
        };
        reader.EntityInfoMap[30] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [5],
            ContentLabel = " ",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("good morning world", result);
    }

    [Fact]
    public async Task RecomposeAsync_MaxDepth_StopsRecursion()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "paragraph",
            Hash = [10],
        };
        reader.SequenceChildren[100] = [(200L, 0)];
        reader.EntityInfoMap[200] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [20],
        };
        reader.SequenceChildren[200] = [(1L, 0)];
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "deep",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        // MaxDepth=0: root entity only, no recursion into children.
        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions { MaxDepth = 0 }, CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RecomposeAsync_MissingEntity_ReturnsEmpty()
    {
        FakeEntityReader reader = new();
        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            999L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RecomposeAsync_AtomWithNoContentLabel_Skipped()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [10],
        };
        reader.SequenceChildren[100] = [(1L, 0), (10L, 1), (2L, 2)];
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "visible",
        };
        reader.EntityInfoMap[10] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [3],
            ContentLabel = " ",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [2],
            ContentLabel = null, // No label.
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("visible ", result);
    }

    [Fact]
    public async Task RecomposeToStreamAsync_WritesUtf8()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [1],
            ContentLabel = "stream",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);
        using MemoryStream ms = new();

        await recomposer.RecomposeToStreamAsync(
            1L, new RecompositionOptions(), ms, CancellationToken.None);

        ms.Position = 0;
        using StreamReader sr = new(ms);
        string text = await sr.ReadToEndAsync();

        Assert.Equal("stream", text);
    }

    [Fact]
    public async Task RecomposeAsync_MixedAtomTypes_CorrectSeparation()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [10],
        };
        reader.SequenceChildren[100] = [(1L, 0), (2L, 1), (3L, 2)];

        // Lemma (space-separated) followed by codepoints (direct concat).
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "lemma",
            Hash = [1],
            ContentLabel = "run",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [2],
            ContentLabel = "!",
        };
        reader.EntityInfoMap[3] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [3],
            ContentLabel = "!",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("run!!", result);
    }

    [Fact]
    public async Task RecomposeAsync_DoesNotTrimOrInventWhitespace()
    {
        FakeEntityReader reader = new();
        reader.EntityInfoMap[100] = new EntityInfo
        {
            EntityTypeCode = "text_composition",
            Hash = [10],
        };
        reader.SequenceChildren[100] = [(1L, 0), (2L, 1), (3L, 2)];
        reader.EntityInfoMap[1] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [1],
            ContentLabel = " ",
        };
        reader.EntityInfoMap[2] = new EntityInfo
        {
            EntityTypeCode = "word_form",
            Hash = [2],
            ContentLabel = "hello",
        };
        reader.EntityInfoMap[3] = new EntityInfo
        {
            EntityTypeCode = "codepoint",
            Hash = [3],
            ContentLabel = "\n",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions(), CancellationToken.None);

        Assert.Equal(" hello\n", result);
    }

    [Fact]
    public async Task RecomposeAsync_UsesFastPathWhenAvailable()
    {
        FakeEntityReader reader = new()
        {
            FastText = "bit-perfect"
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            100L, new RecompositionOptions { MaxDepth = 7 }, CancellationToken.None);

        Assert.Equal("bit-perfect", result);
        Assert.Equal((100L, 7), reader.FastPathRequest);
        Assert.Empty(reader.EntityInfoRequests);
    }

    // ── Fakes ──

    internal sealed class FakeEntityReader : IEntityReader, ITextRecompositionReader
    {
        public Dictionary<long, EntityInfo> EntityInfoMap { get; } = [];
        public Dictionary<long, List<(long ChildEntityId, int Position)>> SequenceChildren { get; } = [];
        public List<IReadOnlyList<long>> EntityInfoRequests { get; } = [];
        public string? FastText { get; init; }
        public (long EntityId, int MaxDepth)? FastPathRequest { get; private set; }

        public Task<IReadOnlyDictionary<byte[], long>> ResolveEntityIdsAsync(
            IReadOnlyList<byte[]> hashes, CancellationToken ct)
        {
            IReadOnlyDictionary<byte[], long> empty = new Dictionary<byte[], long>();
            return Task.FromResult(empty);
        }

        public Task<IReadOnlyDictionary<long, EntityInfo>> GetEntityInfoAsync(
            IReadOnlyList<long> entityIds, CancellationToken ct)
        {
            EntityInfoRequests.Add(entityIds);
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
            return Task.FromResult<IReadOnlyList<(long, string)>>(Array.Empty<(long, string)>());
        }

        public Task<IReadOnlyList<long>> GetOutboundEdgeTargetsAsync(
            long sourceEntityId, string edgeTypeCode, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        }

        public Task<string?> RecomposeTextAsync(long entityId, int maxDepth, CancellationToken ct)
        {
            FastPathRequest = (entityId, maxDepth);
            return Task.FromResult(FastText);
        }
    }
}
