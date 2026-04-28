using System;
using System.Collections.Generic;
using System.IO;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;
using Hartonomous.Recomposers;

namespace Hartonomous.Engine.Tests.Recomposition;

public sealed class TextRecomposerTests
{
    /// <summary>Test helper: long → EntityHandle.</summary>
    private static EntityHandle H(long id, string typeCode = "test")
    {
        byte[] hash = new byte[32];
        BitConverter.GetBytes(id).CopyTo(hash, 0);
        return new EntityHandle(hash, typeCode);
    }

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
        reader.SetEntityInfo(H(1, "word_form"), "hello");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(1, "word_form"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task RecomposeAsync_CompositionWithChildren_ConcatenatesAtoms()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "text_composition"), null);
        reader.SetCompositionChildren(H(100, "text_composition"),
        [
            H(1, "word_form"), H(10, "codepoint"),
            H(2, "word_form"), H(11, "codepoint"),
            H(3, "word_form"),
        ]);
        reader.SetEntityInfo(H(1, "word_form"), "the");
        reader.SetEntityInfo(H(2, "word_form"), "cat");
        reader.SetEntityInfo(H(3, "word_form"), "sat");
        reader.SetEntityInfo(H(10, "codepoint"), " ");
        reader.SetEntityInfo(H(11, "codepoint"), " ");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("the cat sat", result);
    }

    [Fact]
    public async Task RecomposeAsync_CodepointAtoms_ConcatenatedDirectly()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "text_composition"), null);
        reader.SetCompositionChildren(H(100, "text_composition"),
        [
            H(1, "codepoint"), H(2, "codepoint"), H(3, "codepoint"),
        ]);
        reader.SetEntityInfo(H(1, "codepoint"), "c");
        reader.SetEntityInfo(H(2, "codepoint"), "a");
        reader.SetEntityInfo(H(3, "codepoint"), "t");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("cat", result);
    }

    [Fact]
    public async Task RecomposeAsync_NestedComposition_RecursesDepthFirst()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "paragraph"), null);
        reader.SetCompositionChildren(H(100, "paragraph"),
            [H(200, "text_composition"), H(30, "codepoint"), H(300, "text_composition")]);

        reader.SetEntityInfo(H(200, "text_composition"), null);
        reader.SetCompositionChildren(H(200, "text_composition"),
            [H(1, "word_form"), H(10, "codepoint"), H(2, "word_form")]);

        reader.SetEntityInfo(H(300, "text_composition"), null);
        reader.SetCompositionChildren(H(300, "text_composition"), [H(3, "word_form")]);

        reader.SetEntityInfo(H(1, "word_form"), "good");
        reader.SetEntityInfo(H(2, "word_form"), "morning");
        reader.SetEntityInfo(H(3, "word_form"), "world");
        reader.SetEntityInfo(H(10, "codepoint"), " ");
        reader.SetEntityInfo(H(30, "codepoint"), " ");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "paragraph"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("good morning world", result);
    }

    [Fact]
    public async Task RecomposeAsync_MaxDepth_StopsRecursion()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "paragraph"), null);
        reader.SetCompositionChildren(H(100, "paragraph"), [H(200, "text_composition")]);
        reader.SetEntityInfo(H(200, "text_composition"), null);
        reader.SetCompositionChildren(H(200, "text_composition"), [H(1, "word_form")]);
        reader.SetEntityInfo(H(1, "word_form"), "deep");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "paragraph"), new RecompositionOptions { MaxDepth = 0 }, CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RecomposeAsync_MissingEntity_ReturnsEmpty()
    {
        FakeEntityReader reader = new();
        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(999), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task RecomposeAsync_AtomWithNoContentLabel_Skipped()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "text_composition"), null);
        reader.SetCompositionChildren(H(100, "text_composition"),
            [H(1, "word_form"), H(10, "codepoint"), H(2, "word_form")]);
        reader.SetEntityInfo(H(1, "word_form"), "visible");
        reader.SetEntityInfo(H(10, "codepoint"), " ");
        reader.SetEntityInfo(H(2, "word_form"), null); // No label.

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("visible ", result);
    }

    [Fact]
    public async Task RecomposeToStreamAsync_WritesUtf8()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(1, "word_form"), "stream");

        TextRecomposer recomposer = CreateRecomposer(reader);
        using MemoryStream ms = new();

        await recomposer.RecomposeToStreamAsync(
            H(1, "word_form"), new RecompositionOptions(), ms, CancellationToken.None);

        ms.Position = 0;
        using StreamReader sr = new(ms);
        string text = await sr.ReadToEndAsync();

        Assert.Equal("stream", text);
    }

    [Fact]
    public async Task RecomposeAsync_MixedAtomTypes_CorrectSeparation()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "text_composition"), null);
        reader.SetCompositionChildren(H(100, "text_composition"),
            [H(1, "lemma"), H(2, "codepoint"), H(3, "codepoint")]);

        reader.SetEntityInfo(H(1, "lemma"), "run");
        reader.SetEntityInfo(H(2, "codepoint"), "!");
        reader.SetEntityInfo(H(3, "codepoint"), "!");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal("run!!", result);
    }

    [Fact]
    public async Task RecomposeAsync_DoesNotTrimOrInventWhitespace()
    {
        FakeEntityReader reader = new();
        reader.SetEntityInfo(H(100, "text_composition"), null);
        reader.SetCompositionChildren(H(100, "text_composition"),
            [H(1, "codepoint"), H(2, "word_form"), H(3, "codepoint")]);
        reader.SetEntityInfo(H(1, "codepoint"), " ");
        reader.SetEntityInfo(H(2, "word_form"), "hello");
        reader.SetEntityInfo(H(3, "codepoint"), "\n");

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions(), CancellationToken.None);

        Assert.Equal(" hello\n", result);
    }

    [Fact]
    public async Task RecomposeAsync_UsesFastPathWhenAvailable()
    {
        FakeEntityReader reader = new()
        {
            FastText = "bit-perfect",
        };

        TextRecomposer recomposer = CreateRecomposer(reader);

        string result = await recomposer.RecomposeAsync(
            H(100, "text_composition"), new RecompositionOptions { MaxDepth = 7 }, CancellationToken.None);

        Assert.Equal("bit-perfect", result);
        Assert.Equal((H(100, "text_composition"), 7), reader.FastPathRequest);
    }

    // ── Fakes ──

    internal sealed class FakeEntityReader : IEntityReader, ITextRecompositionReader
    {
        private readonly Dictionary<EntityHandle, EntityInfo> _entityInfo = [];
        private readonly Dictionary<EntityHandle, List<EntityHandle>> _children = [];

        public string? FastText { get; init; }
        public (EntityHandle Entity, int MaxDepth)? FastPathRequest { get; private set; }

        public void SetEntityInfo(EntityHandle handle, string? contentLabel)
        {
            _entityInfo[handle] = new EntityInfo
            {
                Handle = handle,
                ContentLabel = contentLabel,
            };
        }

        public void SetCompositionChildren(EntityHandle parent, IReadOnlyList<EntityHandle> children)
        {
            _children[parent] = [.. children];
        }

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
            }
            return Task.FromResult<IReadOnlyDictionary<EntityHandle, EntityInfo>>(result);
        }

        public Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
            EntityHandle parent, CancellationToken ct)
        {
            if (!_children.TryGetValue(parent, out List<EntityHandle>? list))
            {
                return Task.FromResult<IReadOnlyList<(EntityHandle, int)>>([]);
            }
            (EntityHandle, int)[] withPos = new (EntityHandle, int)[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                withPos[i] = (list[i], i + 1);
            }
            return Task.FromResult<IReadOnlyList<(EntityHandle, int)>>(withPos);
        }

        public Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
            IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EdgeHandle, EdgeInfo>>(
                new Dictionary<EdgeHandle, EdgeInfo>());

        public Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
            string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
            EntityHandle source, string edgeTypeCode, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct)
        {
            FastPathRequest = (root, maxDepth);
            return Task.FromResult(FastText);
        }
    }
}
